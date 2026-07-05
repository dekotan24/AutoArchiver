using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AutoArchiver.Utils;

namespace AutoArchiver.Core
{
	/// <summary>
	/// 外部ツール（7z.exe / Rar.exe）を起動してアーカイブの作成・テストを行うエンジン。
	/// 進捗パーセントは標準出力のパースで取得する。
	/// </summary>
	public static class CompressionEngine
	{
		/// <summary>7z / rar の出力から進捗パーセントを拾う（例: " 42%"）。
		/// ファイル名等に含まれる%の誤検出を避けるため、直前が空白・行頭のものだけ拾う</summary>
		private static readonly Regex ProgressRegex = new(@"(?:^|[\s\r])(\d{1,3})%", RegexOptions.Compiled);

		/// <summary>
		/// ファイル・フォルダ（混在可）を指定形式で1つのアーカイブに圧縮する。
		/// 書庫内は各アイテムの名前（フォルダはフォルダ名ごと、ファイルはファイル名）で格納される。
		/// </summary>
		/// <param name="format">出力形式</param>
		/// <param name="sources">圧縮対象のファイル・フォルダのフルパス</param>
		/// <param name="archivePath">出力アーカイブのフルパス</param>
		/// <param name="options">パスワード・リカバリレコード・除外パターン</param>
		/// <param name="onProgress">進捗パーセント（0-100）の通知</param>
		public static async Task CompressAsync(
			ArchiveFormat format,
			IReadOnlyList<string> sources,
			string archivePath,
			CompressionOptions options,
			Action<int>? onProgress,
			CancellationToken cancellationToken)
		{
			string exe;
			string args;

			// パスは末尾の \ を除去してからクォート（末尾\はクォートをエスケープしてしまうため）
			var quoted = new List<string>(sources.Count);
			foreach (string source in sources)
			{
				quoted.Add($"\"{source.TrimEnd('\\', '/')}\"");
			}
			string sourceArgs = string.Join(" ", quoted);

			switch (format)
			{
				case ArchiveFormat.SevenZip:
					exe = RequireSevenZip();
					// -mx=9: 最高圧縮 / -ms=on: ソリッド圧縮 / -ssw: 共有中ファイルも圧縮
					// -bsp1: 進捗を標準出力へ / -mhe=on: ヘッダ（ファイル名）も暗号化
					string pw7z = options.HasPassword ? $" \"-p{options.Password}\" -mhe=on" : "";
					args = $"a -t7z -mx=9 -ms=on -ssw -bsp1 -y{pw7z}{BuildExcludeArgs7z(options)} \"{archivePath}\" {sourceArgs}";
					break;

				case ArchiveFormat.ZipStore:
					exe = RequireSevenZip();
					// -mx=0: 無圧縮 (store)。既圧縮データ主体のときの高速格納
					string pwZip = options.HasPassword ? $" \"-p{options.Password}\"" : "";
					args = $"a -tzip -mx=0 -ssw -bsp1 -y{pwZip}{BuildExcludeArgs7z(options)} \"{archivePath}\" {sourceArgs}";
					break;

				case ArchiveFormat.Rar:
					exe = ToolLocator.RarPath ?? throw new InvalidOperationException("Rar.exe が見つかりません。");
					// -ma5: RAR5形式を明示 / -m5: 最高圧縮 / -md256m: 辞書256MB / -s: ソリッド
					// -ep1: ベースフォルダを除いたパス格納 / -r: 再帰 / -y: 全て既定回答
					// -hp: ヘッダ含め暗号化 / -rrN%: リカバリレコード
					string pwRar = options.HasPassword ? $" \"-hp{options.Password}\"" : "";
					string rr = options.RecoveryRecordPercent > 0 ? $" -rr{options.RecoveryRecordPercent}%" : "";
					args = $"a -ma5 -m5 -md256m -s -ep1 -r -y{pwRar}{rr}{BuildExcludeArgsRar(options)} \"{archivePath}\" {sourceArgs}";
					break;

				default:
					throw new ArgumentOutOfRangeException(nameof(format));
			}

			// Windowsのコマンドライン長上限（約32K文字）を超える大量アイテムは受けられない
			if (args.Length > 30000)
			{
				throw new CompressionException(
					$"一度に渡されたアイテムが多すぎます（コマンドライン長の上限超過）。親フォルダごと圧縮するか、回数を分けてください。");
			}

			int exitCode = await RunProcessAsync(exe, args, onProgress, cancellationToken);

			// 7z: 0=成功, 1=警告（一部ファイルがロック中等。書庫自体は有効） / rar: 0=成功, 1=警告
			if (exitCode != 0 && exitCode != 1)
			{
				throw new CompressionException($"圧縮に失敗しました (exit code {exitCode})");
			}
		}

		/// <summary>
		/// アーカイブの整合性テスト（書庫テスト）を実行する。
		/// </summary>
		public static async Task<bool> TestArchiveAsync(
			ArchiveFormat format,
			string archivePath,
			CompressionOptions options,
			Action<int>? onProgress,
			CancellationToken cancellationToken)
		{
			string exe;
			string args;

			// パスワード付き書庫のテストには同じパスワードが必要
			if (format == ArchiveFormat.Rar)
			{
				exe = ToolLocator.RarPath ?? throw new InvalidOperationException("Rar.exe が見つかりません。");
				string pw = options.HasPassword ? $" \"-p{options.Password}\"" : "";
				args = $"t -y{pw} \"{archivePath}\"";
			}
			else
			{
				exe = RequireSevenZip();
				string pw = options.HasPassword ? $" \"-p{options.Password}\"" : "";
				args = $"t -bsp1 -y{pw} \"{archivePath}\"";
			}

			int exitCode = await RunProcessAsync(exe, args, onProgress, cancellationToken);
			// テストは警告も不合格扱いにする（完全性の保証が目的のため）
			return exitCode == 0;
		}

		/// <summary>
		/// サンプルファイル群を一時アーカイブへ圧縮して、実測の圧縮率（出力/入力、0.0〜1.0超）を返す。
		/// 層化サンプルベンチ用。ベンチ後の一時ファイルは削除する。
		/// </summary>
		/// <param name="listFilePath">対象ファイルのフルパスを列挙したリストファイル（UTF-8）</param>
		/// <param name="inputTotalBytes">サンプルの合計入力サイズ</param>
		public static async Task<double> BenchmarkAsync(
			ArchiveFormat format,
			string listFilePath,
			long inputTotalBytes,
			CancellationToken cancellationToken)
		{
			if (inputTotalBytes <= 0)
			{
				return 1.0;
			}

			string tempArchive = Path.Combine(
				Path.GetTempPath(),
				$"AutoArchiver_bench_{Guid.NewGuid():N}.{(format == ArchiveFormat.Rar ? "rar" : "7z")}");

			try
			{
				string exe;
				string args;

				// ベンチは圧縮レベル等は本番と同じだが、ソリッドだけはオフにする。
				// サンプル（特に巨大ファイルの分割チャンク）をソリッドで束ねると、本番では
				// 辞書窓外でマッチしないデータ同士が人工的にマッチして「縮みすぎ」予測になるため。
				if (format == ArchiveFormat.Rar)
				{
					exe = ToolLocator.RarPath ?? throw new InvalidOperationException("Rar.exe が見つかりません。");
					// -s-: ソリッド無効
					// -scfl: リストファイルをUTF-8として読む（無いと日本語名ファイルを黙って取りこぼしベンチが歪む）
					args = $"a -ma5 -m5 -md256m -s- -ep1 -scfl -y \"{tempArchive}\" @\"{listFilePath}\"";
				}
				else
				{
					exe = RequireSevenZip();
					// -ms=off: ソリッド無効
					// -spf2: リストファイルのフルパスを許可 / -scsUTF-8: リストファイルの文字コードを明示
					args = $"a -t7z -mx=9 -ms=off -ssw -spf2 -scsUTF-8 -y \"{tempArchive}\" @\"{listFilePath}\"";
				}

				int exitCode = await RunProcessAsync(exe, args, null, cancellationToken);
				if ((exitCode != 0 && exitCode != 1) || !File.Exists(tempArchive))
				{
					// ベンチ失敗時は「縮まない」扱いにして安全側へ倒す
					return 1.0;
				}

				long outSize = new FileInfo(tempArchive).Length;
				return (double)outSize / inputTotalBytes;
			}
			finally
			{
				TryDelete(tempArchive);
			}
		}

		/// <summary>
		/// 外部プロセスを実行し、標準出力から進捗を拾いつつ完了を待つ。
		/// キャンセル時はプロセスをKillする。
		/// </summary>
		private static async Task<int> RunProcessAsync(
			string exePath,
			string arguments,
			Action<int>? onProgress,
			CancellationToken cancellationToken)
		{
			var psi = new ProcessStartInfo
			{
				FileName = exePath,
				Arguments = arguments,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
			};

			using var process = new Process { StartInfo = psi };

			var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
			process.EnableRaisingEvents = true;
			process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

			if (!process.Start())
			{
				throw new CompressionException($"プロセスを起動できませんでした: {exePath}");
			}

			// 進捗パース: 7z/rarは "  42%" のような形で出力バッファへ書く（改行ではなくCRで更新される
			// ことがあるため、行単位でなく文字ストリームから拾う）
			var stdoutTask = Task.Run(async () =>
			{
				var buffer = new char[256];
				var lineBuf = new StringBuilder();
				while (true)
				{
					int read = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length);
					if (read <= 0)
					{
						break;
					}
					lineBuf.Append(buffer, 0, read);
					// 直近チャンクから最後に見つかったパーセントを採用
					var matches = ProgressRegex.Matches(lineBuf.ToString());
					if (matches.Count > 0)
					{
						if (int.TryParse(matches[^1].Groups[1].Value, out int pct) && pct >= 0 && pct <= 100)
						{
							onProgress?.Invoke(pct);
						}
						lineBuf.Clear();
					}
					else if (lineBuf.Length > 4096)
					{
						lineBuf.Clear();
					}
				}
			}, CancellationToken.None);

			// stderrは読み捨て（読まないとバッファ詰まりでデッドロックするため必ず読む）
			var stderrTask = process.StandardError.ReadToEndAsync();

			using var registration = cancellationToken.Register(() =>
			{
				try
				{
					if (!process.HasExited)
					{
						process.Kill(entireProcessTree: true);
					}
				}
				catch
				{
					// 既に終了していた場合等は無視
				}
			});

			int exitCode = await tcs.Task;
			await stdoutTask;
			await stderrTask;

			cancellationToken.ThrowIfCancellationRequested();
			return exitCode;
		}

		/// <summary>7z/zip用の除外引数を組み立てる（-xr! は全階層再帰の除外）</summary>
		private static string BuildExcludeArgs7z(CompressionOptions options)
		{
			var sb = new StringBuilder();
			foreach (string pattern in options.ExcludePatterns)
			{
				if (!string.IsNullOrWhiteSpace(pattern))
				{
					sb.Append($" \"-xr!{pattern.Trim()}\"");
				}
			}
			return sb.ToString();
		}

		/// <summary>RAR用の除外引数を組み立てる（ルート直下と全階層の両方をカバー）</summary>
		private static string BuildExcludeArgsRar(CompressionOptions options)
		{
			var sb = new StringBuilder();
			foreach (string pattern in options.ExcludePatterns)
			{
				if (!string.IsNullOrWhiteSpace(pattern))
				{
					string p = pattern.Trim();
					sb.Append($" \"-x{p}\" \"-x*\\{p}\"");
				}
			}
			return sb.ToString();
		}

		private static string RequireSevenZip()
		{
			return ToolLocator.SevenZipPath
				?? throw new InvalidOperationException("7z.exe が見つかりません。7-Zipをインストールしてください。");
		}

		private static void TryDelete(string path)
		{
			try
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
			catch
			{
				// 一時ファイルの削除失敗は致命的でないため無視
			}
		}
	}

	/// <summary>圧縮処理の失敗を表す例外</summary>
	public class CompressionException : Exception
	{
		public CompressionException(string message) : base(message) { }
	}
}
