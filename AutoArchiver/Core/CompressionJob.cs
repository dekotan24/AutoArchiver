namespace AutoArchiver.Core
{
	/// <summary>ジョブの進行フェーズ</summary>
	public enum JobPhase
	{
		Analyzing,
		Benchmarking,
		Compressing,
		Testing,
		Done,
	}

	/// <summary>ジョブ進捗の通知内容</summary>
	public class JobProgress
	{
		public JobPhase Phase { get; init; }

		/// <summary>フェーズ内の進捗（0-100）。不定のときは -1</summary>
		public int Percent { get; init; } = -1;

		public JobProgress(JobPhase phase, int percent = -1)
		{
			Phase = phase;
			Percent = percent;
		}
	}

	/// <summary>ジョブの実行結果</summary>
	public class JobResult
	{
		public bool Success { get; init; }
		public string? ErrorMessage { get; init; }
		public string? ArchivePath { get; init; }
		public FormatDecision? Decision { get; init; }
		public long OriginalSize { get; init; }
		public long ArchiveSize { get; init; }
		public TimeSpan Elapsed { get; init; }
	}

	/// <summary>
	/// 1書庫分の圧縮パイプライン: 分析 → 形式決定（必要ならベンチ）→ 圧縮 → 書庫テスト。
	/// 対象はファイル・フォルダ混在の複数アイテム（単一でも可）で、まとめて1つの書庫になる。
	/// 元アイテムの削除確認はUI側の責務（このクラスは元アイテムに手を付けない）。
	/// </summary>
	public class CompressionJob
	{
		private readonly List<string> _items;
		private readonly string? _outputDirectory;
		private readonly CompressionOptions _options;
		private readonly Action<string> _log;
		private readonly Action<JobProgress> _progress;

		/// <summary>このジョブが実際に書き出しているアーカイブのパス（掃除用）</summary>
		private string? _currentArchivePath;

		/// <summary>ジョブの表示名（単一ならアイテム名、複数なら「名前 他N件」）</summary>
		public string DisplayName
		{
			get
			{
				string first = Path.GetFileName(_items[0].TrimEnd('\\', '/'));
				return _items.Count == 1 ? first : $"{first} 他{_items.Count - 1}件";
			}
		}

		/// <param name="items">圧縮対象のファイル・フォルダ（まとめて1書庫になる）</param>
		/// <param name="outputDirectory">アーカイブの保存先。null・空なら既定（下記BuildArchivePath参照）</param>
		/// <param name="options">パスワード・リカバリレコード・除外パターン。nullなら既定（すべて無効）</param>
		public CompressionJob(IEnumerable<string> items, string? outputDirectory, CompressionOptions? options, Action<string> log, Action<JobProgress> progress)
		{
			_items = new List<string>(items);
			if (_items.Count == 0)
			{
				throw new ArgumentException("圧縮対象が空です。", nameof(items));
			}
			_outputDirectory = string.IsNullOrWhiteSpace(outputDirectory) ? null : outputDirectory;
			_options = options ?? CompressionOptions.Default;
			_log = log;
			_progress = progress;
		}

		public async Task<JobResult> RunAsync(CancellationToken cancellationToken)
		{
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();

			try
			{
				// --- フェーズ1: 分析 ---
				_progress(new JobProgress(JobPhase.Analyzing));
				if (_items.Count == 1)
				{
					_log($"分析中: {_items[0]}");
				}
				else
				{
					_log($"分析中: {_items.Count}アイテムをまとめて1書庫にします");
					foreach (string item in _items)
					{
						_log($"  ・{item}");
					}
				}

				var analysis = await Task.Run(() => FolderAnalyzer.Analyze(_items, cancellationToken), cancellationToken);

				if (analysis.TotalFileCount == 0)
				{
					return Fail("対象にファイルがありません。", stopwatch);
				}

				_log($"  ファイル {analysis.TotalFileCount:N0} 件 / 合計 {FormatSelector.FormatBytes(analysis.TotalSize)}");
				_log($"  内訳: 既圧縮 {analysis.SizeRatio(FileCategory.Compressed):P0} / 圧縮可能 {analysis.SizeRatio(FileCategory.Compressible):P0} / 不明 {analysis.SizeRatio(FileCategory.Unknown):P0}");
				if (analysis.InaccessibleCount > 0)
				{
					_log($"  ⚠ アクセスできない項目が {analysis.InaccessibleCount} 件あります（スキップ）");
				}

				// --- フェーズ2: 形式決定（必要な場合のみ層化ベンチ実行） ---
				_progress(new JobProgress(JobPhase.Benchmarking));
				var decision = await FormatSelector.DecideAsync(analysis, _options, _log, cancellationToken);

				_log($"形式決定: {decision.DisplayName}");
				_log($"  理由: {decision.Reason}");

				// --- フェーズ3: 圧縮 ---
				string archivePath = BuildArchivePath(decision.Extension);
				CheckDiskSpace(archivePath, analysis, decision);
				_currentArchivePath = archivePath;
				_progress(new JobProgress(JobPhase.Compressing, 0));

				if (_options.HasPassword)
				{
					_log("パスワード付き書庫として作成します。");
				}
				if (decision.Format == ArchiveFormat.Rar && _options.RecoveryRecordPercent > 0)
				{
					_log($"リカバリレコード {_options.RecoveryRecordPercent}% を付与します。");
				}
				_log($"圧縮開始 → {archivePath}");

				await CompressionEngine.CompressAsync(
					decision.Format,
					_items,
					archivePath,
					_options,
					pct => _progress(new JobProgress(JobPhase.Compressing, pct)),
					cancellationToken);

				if (!File.Exists(archivePath))
				{
					return Fail("圧縮は終了しましたがアーカイブが見つかりません。", stopwatch);
				}

				long archiveSize = new FileInfo(archivePath).Length;
				_log($"圧縮完了: {FormatSelector.FormatBytes(analysis.TotalSize)} → {FormatSelector.FormatBytes(archiveSize)} ({(double)archiveSize / Math.Max(1, analysis.TotalSize):P1})");

				// --- 事後セーフティネット: 圧縮したのに元より大きくなったら無圧縮ZIPで作り直す ---
				// （ベンチ予測が外れた・既圧縮の再圧縮等。無圧縮ZIP自体の格納オーバーヘッドは許容）
				if (decision.Format != ArchiveFormat.ZipStore && archiveSize > analysis.TotalSize)
				{
					_log($"⚠ 圧縮結果（{FormatSelector.FormatBytes(archiveSize)}）が元（{FormatSelector.FormatBytes(analysis.TotalSize)}）より大きいため、無圧縮ZIPで作り直します。");
					TryDelete(archivePath);

					decision = new FormatDecision(
						ArchiveFormat.ZipStore,
						$"{decision.DisplayName}で圧縮した結果が元より大きくなったため、無圧縮ZIPに切り替え");
					archivePath = BuildArchivePath(decision.Extension);
					_currentArchivePath = archivePath;
					_progress(new JobProgress(JobPhase.Compressing, 0));
					_log($"再作成開始 → {archivePath}");

					await CompressionEngine.CompressAsync(
						decision.Format,
						_items,
						archivePath,
						_options,
						pct => _progress(new JobProgress(JobPhase.Compressing, pct)),
						cancellationToken);

					if (!File.Exists(archivePath))
					{
						return Fail("再作成は終了しましたがアーカイブが見つかりません。", stopwatch);
					}
					archiveSize = new FileInfo(archivePath).Length;
					_log($"再作成完了: {FormatSelector.FormatBytes(analysis.TotalSize)} → {FormatSelector.FormatBytes(archiveSize)} ({(double)archiveSize / Math.Max(1, analysis.TotalSize):P1})");
				}

				// --- フェーズ4: 書庫テスト ---
				_progress(new JobProgress(JobPhase.Testing, 0));
				_log("書庫テスト実行中…");

				bool testOk = await CompressionEngine.TestArchiveAsync(
					decision.Format,
					archivePath,
					_options,
					pct => _progress(new JobProgress(JobPhase.Testing, pct)),
					cancellationToken);

				if (!testOk)
				{
					// テスト不合格の書庫は信用できないので削除して失敗扱い
					_log("✖ 書庫テスト不合格。アーカイブを削除します。");
					TryDelete(archivePath);
					return Fail("書庫テストに失敗しました。元のファイル・フォルダはそのまま残っています。", stopwatch);
				}

				_log("✔ 書庫テスト合格");
				_progress(new JobProgress(JobPhase.Done, 100));
				stopwatch.Stop();

				return new JobResult
				{
					Success = true,
					ArchivePath = archivePath,
					Decision = decision,
					OriginalSize = analysis.TotalSize,
					ArchiveSize = archiveSize,
					Elapsed = stopwatch.Elapsed,
				};
			}
			catch (OperationCanceledException)
			{
				// キャンセル時は作りかけのアーカイブを掃除する
				_log($"キャンセルされました: {DisplayName}");
				CleanupPartialArchives();
				throw;
			}
			catch (Exception ex)
			{
				CleanupPartialArchives();
				return Fail(ex.Message, stopwatch);
			}
		}

		/// <summary>
		/// 出力アーカイブのパスを組み立てる。
		/// 書庫名: 単一アイテムならその名前（ファイルは拡張子抜き）、
		///         複数アイテムで親フォルダが共通ならその親フォルダ名、バラバラなら先頭アイテム名。
		/// 保存先: 指定があればそこ、無ければ先頭アイテムの親フォルダ。
		/// 同名ファイルが既にあるときは「名前 (2).拡張子」のように連番を付ける。
		/// </summary>
		private string BuildArchivePath(string extension)
		{
			string firstTrimmed = _items[0].TrimEnd('\\', '/');
			string firstParent = Path.GetDirectoryName(firstTrimmed) ?? firstTrimmed;

			// --- 書庫のベース名を決める ---
			string baseName;
			if (_items.Count == 1)
			{
				baseName = File.Exists(firstTrimmed)
					? Path.GetFileNameWithoutExtension(firstTrimmed)
					: Path.GetFileName(firstTrimmed);
			}
			else
			{
				string? commonParent = GetCommonParent();
				string parentName = commonParent != null ? Path.GetFileName(commonParent.TrimEnd('\\', '/')) : "";
				baseName = parentName.Length > 0 ? parentName : Path.GetFileName(firstTrimmed);
			}
			if (string.IsNullOrWhiteSpace(baseName))
			{
				baseName = "archive"; // 拡張子だけのファイル名（.gitignore等）などの保険
			}

			// --- 保存先を決める ---
			string outputDir = firstParent;
			if (_outputDirectory != null)
			{
				outputDir = _outputDirectory;
			}

			// 圧縮対象フォルダの中への出力は、書庫が圧縮対象自身に含まれる事故になるため拒否
			string fullOut = Path.GetFullPath(outputDir).TrimEnd('\\', '/');
			foreach (string item in _items)
			{
				if (!Directory.Exists(item))
				{
					continue;
				}
				string fullSrc = Path.GetFullPath(item.TrimEnd('\\', '/'));
				if (string.Equals(fullOut, fullSrc, StringComparison.OrdinalIgnoreCase) ||
					fullOut.StartsWith(fullSrc + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					throw new CompressionException($"保存先が圧縮対象フォルダの中になっています。別の場所を指定してください。（{item}）");
				}
			}

			Directory.CreateDirectory(outputDir);

			string candidate = Path.Combine(outputDir, $"{baseName}.{extension}");
			int counter = 2;
			while (File.Exists(candidate))
			{
				candidate = Path.Combine(outputDir, $"{baseName} ({counter}).{extension}");
				counter++;
			}
			return candidate;
		}

		/// <summary>
		/// 出力先ドライブの空き容量を、予測書庫サイズ（安全率1.2+余裕100MB）と比較して
		/// 明らかに足りない場合は開始前に失敗させる（数十分走った後のディスク不足死を防ぐ）。
		/// </summary>
		private void CheckDiskSpace(string archivePath, FolderAnalysis analysis, FormatDecision decision)
		{
			const long Margin = 100L * 1024 * 1024;

			// ベンチ予測が無い場合（7z即決等）は「縮まない」前提で安全側に見積もる
			double ratio = decision.PredictedRatio ?? 1.0;
			long expected = (long)(analysis.TotalSize * Math.Min(1.0, ratio) * 1.2) + Margin;

			try
			{
				string? root = Path.GetPathRoot(Path.GetFullPath(archivePath));
				if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\"))
				{
					return; // UNCパス等はDriveInfoで測れないためスキップ
				}
				var drive = new DriveInfo(root);
				if (drive.AvailableFreeSpace < expected)
				{
					throw new CompressionException(
						$"出力先ドライブ {root} の空き容量が不足しています" +
						$"（空き {FormatSelector.FormatBytes(drive.AvailableFreeSpace)} / 必要見込み {FormatSelector.FormatBytes(expected)}）。");
				}
				_log($"空き容量OK: {root} 空き {FormatSelector.FormatBytes(drive.AvailableFreeSpace)} / 必要見込み {FormatSelector.FormatBytes(expected)}");
			}
			catch (CompressionException)
			{
				throw;
			}
			catch
			{
				// ドライブ情報が取れない環境（仮想ドライブ等）ではチェックせず続行
			}
		}

		/// <summary>全アイテムの親フォルダが共通ならそのパスを、バラバラならnullを返す</summary>
		private string? GetCommonParent()
		{
			string? common = null;
			foreach (string item in _items)
			{
				string? parent = Path.GetDirectoryName(item.TrimEnd('\\', '/'));
				if (parent == null)
				{
					return null;
				}
				if (common == null)
				{
					common = parent;
				}
				else if (!string.Equals(common, parent, StringComparison.OrdinalIgnoreCase))
				{
					return null;
				}
			}
			return common;
		}

		/// <summary>キャンセル・失敗時に、このジョブが作りかけたアーカイブだけを削除する</summary>
		private void CleanupPartialArchives()
		{
			if (_currentArchivePath == null)
			{
				return; // まだ圧縮フェーズに入っていない
			}
			TryDelete(_currentArchivePath);
			_currentArchivePath = null;
		}

		private JobResult Fail(string message, System.Diagnostics.Stopwatch stopwatch)
		{
			stopwatch.Stop();
			_log($"✖ 失敗: {message}");
			return new JobResult { Success = false, ErrorMessage = message, Elapsed = stopwatch.Elapsed };
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
				// 削除失敗は無視
			}
		}
	}
}
