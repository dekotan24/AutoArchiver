using System.Text;
using AutoArchiver.Utils;

namespace AutoArchiver.Core
{
	/// <summary>
	/// フォルダの内容から最適なアーカイブ形式を自動選択する。
	///
	/// 戦略（ハイブリッド方式）:
	///  1. 拡張子ヒューリスティックで明白なケースは即決
	///     - 既圧縮データがサイズの95%以上 → 無圧縮ZIP（縮み代がないので時間を掛けない）
	///     - 圧縮可能データが75%以上かつ不明が少ない → 7z最高圧縮（このケースはほぼ常勝）
	///  2. 混在・不明形式が多いケースは「層化サンプルベンチ」で実測
	///     - フォルダ全体から無作為に取ると偏るため、カテゴリごとにサンプルを取って
	///       カテゴリ別圧縮率を実測 → サイズ構成比で加重合成して全体の圧縮率を予測する
	///     - 予測圧縮率がほぼ100%なら無圧縮ZIP、それ以外は 7z vs RAR の実測勝者
	/// </summary>
	public static class FormatSelector
	{
		/// <summary>「ほぼ全部既圧縮」とみなすサイズ構成比の閾値</summary>
		private const double CompressedDominantRatio = 0.95;

		/// <summary>「圧縮可能データ主体」とみなすサイズ構成比の閾値</summary>
		private const double CompressibleDominantRatio = 0.75;

		/// <summary>不明カテゴリがこの比率を超えたらヒューリスティック即決を避けてベンチに回す</summary>
		private const double UnknownBenchThreshold = 0.15;

		/// <summary>実測ベンチでこの圧縮率（出力/入力）以上なら「縮まない」と判断して無圧縮ZIPにする。
		/// 小サンプルのソリッド圧縮は本番（全データ束ね）よりソリッド利得を過小評価するため、
		/// store落ちは「よほど確実に縮まないとき」だけに絞る（ボーダー帯は7z/RAR側へ倒す）</summary>
		private const double StoreFallbackRatio = 0.985;

		/// <summary>ベンチのカテゴリごとのサンプル上限（ファイル数）</summary>
		private const int BenchMaxFilesPerCategory = 12;

		/// <summary>ベンチのカテゴリごとのサンプル上限（バイト）</summary>
		private const long BenchMaxBytesPerCategory = 32L * 1024 * 1024; // 32MB

		/// <summary>
		/// 分析結果から出力形式を決定する。必要な場合のみ層化サンプルベンチを実行する。
		/// </summary>
		public static async Task<FormatDecision> DecideAsync(
			FolderAnalysis analysis,
			Action<string>? onLog,
			CancellationToken cancellationToken)
		{
			double compressedRatio = analysis.SizeRatio(FileCategory.Compressed);
			double compressibleRatio = analysis.SizeRatio(FileCategory.Compressible);
			double unknownRatio = analysis.SizeRatio(FileCategory.Unknown);

			// --- ステップ1: ヒューリスティック即決 ---

			if (analysis.TotalSize == 0)
			{
				return new FormatDecision(ArchiveFormat.ZipStore, "中身が空（0バイト）のため無圧縮ZIPで格納");
			}

			if (compressedRatio >= CompressedDominantRatio)
			{
				return new FormatDecision(
					ArchiveFormat.ZipStore,
					$"サイズの{compressedRatio:P0}が既圧縮データ（動画・画像・書庫等）。再圧縮の縮み代がないため無圧縮ZIPで高速格納");
			}

			if (compressibleRatio >= CompressibleDominantRatio && unknownRatio < UnknownBenchThreshold)
			{
				return new FormatDecision(
					ArchiveFormat.SevenZip,
					$"サイズの{compressibleRatio:P0}が圧縮可能データ（テキスト・バイナリ等）。この構成は7z LZMA2最高圧縮がほぼ常勝");
			}

			// --- ステップ2: 混在・不明が多い → 層化サンプルベンチ ---

			onLog?.Invoke($"構成が混在（既圧縮 {compressedRatio:P0} / 圧縮可能 {compressibleRatio:P0} / 不明 {unknownRatio:P0}）のため、カテゴリ別サンプルで実測ベンチを行います…");

			return await RunStratifiedBenchmarkAsync(analysis, onLog, cancellationToken);
		}

		/// <summary>
		/// 層化サンプルベンチ本体。
		/// カテゴリごとにサンプルファイルを選び、7z / RAR それぞれで実測圧縮率を測って
		/// サイズ構成比で加重合成 → 全体予測圧縮率の低い（=よく縮む）形式を選ぶ。
		/// </summary>
		private static async Task<FormatDecision> RunStratifiedBenchmarkAsync(
			FolderAnalysis analysis,
			Action<string>? onLog,
			CancellationToken cancellationToken)
		{
			bool useRar = ToolLocator.HasRar;

			// カテゴリ別の実測圧縮率（サンプルが取れなかったカテゴリは保守的な既定値を使う）
			var ratio7z = new Dictionary<FileCategory, double>();
			var ratioRar = new Dictionary<FileCategory, double>();
			var details = new List<string>();

			foreach (FileCategory category in Enum.GetValues<FileCategory>())
			{
				long categorySize = analysis.SizeByCategory[category];
				if (categorySize <= 0)
				{
					continue;
				}

				var samples = PickSamples(analysis.FilesByCategory[category]);

				// 巨大ファイル（ISOイメージ等）はベンチが本番より重くなるため、
				// 先頭チャンクだけ一時ファイルに切り出してベンチ対象にする
				var benchPaths = new List<string>();
				var tempChunks = new List<string>();
				long sampleBytes = 0;
				foreach (var f in samples)
				{
					if (f.Length > BenchMaxBytesPerCategory)
					{
						var chunks = await CopySpreadChunksAsync(f, BenchMaxBytesPerCategory, cancellationToken);
						tempChunks.AddRange(chunks);
						benchPaths.AddRange(chunks);
						sampleBytes += BenchMaxBytesPerCategory;
					}
					else
					{
						benchPaths.Add(f.FullName);
						sampleBytes += f.Length;
					}
				}

				if (benchPaths.Count == 0 || sampleBytes == 0)
				{
					// サンプル不能（0バイトファイルのみ等）: 縮まない扱い
					ratio7z[category] = 1.0;
					ratioRar[category] = 1.0;
					continue;
				}

				// サンプルリストをUTF-8のリストファイルに書き出して7z/rarへ渡す
				string listFile = Path.Combine(Path.GetTempPath(), $"AutoArchiver_list_{Guid.NewGuid():N}.txt");
				try
				{
					var sb = new StringBuilder();
					foreach (string path in benchPaths)
					{
						sb.AppendLine(path);
					}
					await File.WriteAllTextAsync(listFile, sb.ToString(), new UTF8Encoding(false), cancellationToken);

					double r7 = await CompressionEngine.BenchmarkAsync(ArchiveFormat.SevenZip, listFile, sampleBytes, cancellationToken);
					ratio7z[category] = r7;

					if (useRar)
					{
						double rr = await CompressionEngine.BenchmarkAsync(ArchiveFormat.Rar, listFile, sampleBytes, cancellationToken);
						ratioRar[category] = rr;
					}

					string rarText = useRar ? $" / RAR {ratioRar[category]:P1}" : "";
					string line = $"  [{CategoryLabel(category)}] サンプル{samples.Count}件 {FormatBytes(sampleBytes)} → 7z {r7:P1}{rarText}";
					details.Add(line);
					onLog?.Invoke(line);
				}
				finally
				{
					try { File.Delete(listFile); } catch { /* 一時ファイル削除失敗は無視 */ }
					foreach (string chunk in tempChunks)
					{
						try { File.Delete(chunk); } catch { /* 同上 */ }
					}
				}
			}

			// サイズ構成比で加重合成して「フォルダ全体の予測圧縮率」を出す
			double predicted7z = WeightedRatio(analysis, ratio7z);
			double predictedRar = useRar ? WeightedRatio(analysis, ratioRar) : double.MaxValue;

			string summary = useRar
				? $"全体予測圧縮率: 7z {predicted7z:P1} / RAR {predictedRar:P1}"
				: $"全体予測圧縮率: 7z {predicted7z:P1}";
			details.Add(summary);
			onLog?.Invoke(summary);

			// ほぼ縮まないなら無圧縮ZIPで時間を節約
			double best = Math.Min(predicted7z, predictedRar);
			FormatDecision decision;
			if (best >= StoreFallbackRatio)
			{
				decision = new FormatDecision(
					ArchiveFormat.ZipStore,
					$"実測ベンチの予測圧縮率が{best:P1}でほぼ縮まないため、無圧縮ZIPで高速格納");
			}
			else if (predictedRar < predicted7z)
			{
				decision = new FormatDecision(
					ArchiveFormat.Rar,
					$"実測ベンチでRAR5が勝利（RAR {predictedRar:P1} vs 7z {predicted7z:P1}）");
			}
			else
			{
				string vsText = useRar ? $"（7z {predicted7z:P1} vs RAR {predictedRar:P1}）" : $"（{predicted7z:P1}）";
				decision = new FormatDecision(
					ArchiveFormat.SevenZip,
					$"実測ベンチで7zが勝利{vsText}");
			}

			decision.BenchmarkDetails.AddRange(details);
			decision.PredictedRatio = Math.Min(1.0, best);
			return decision;
		}

		/// <summary>
		/// カテゴリのファイルリストからベンチ用サンプルを選ぶ。
		/// 全体圧縮率はサイズの大きいファイルに支配されるため「サイズ降順の上位」を軸に、
		/// 代表性を持たせるためサイズ順の等間隔ストライドからも拾う。
		/// </summary>
		private static List<FileInfo> PickSamples(List<FileInfo> files)
		{
			var sorted = new List<FileInfo>(files);
			sorted.Sort((a, b) => b.Length.CompareTo(a.Length));

			var picked = new List<FileInfo>();
			long pickedBytes = 0;

			// 上位（サイズ支配層）から半分
			int topCount = Math.Min(BenchMaxFilesPerCategory / 2, sorted.Count);
			for (int i = 0; i < topCount; i++)
			{
				if (pickedBytes + sorted[i].Length > BenchMaxBytesPerCategory && picked.Count > 0)
				{
					break;
				}
				picked.Add(sorted[i]);
				pickedBytes += sorted[i].Length;
			}

			// 残り半分はサイズ順の等間隔ストライドで中間層〜小サイズ層から拾う
			int remainSlots = BenchMaxFilesPerCategory - picked.Count;
			if (remainSlots > 0 && sorted.Count > topCount)
			{
				int stride = Math.Max(1, (sorted.Count - topCount) / remainSlots);
				for (int i = topCount; i < sorted.Count && picked.Count < BenchMaxFilesPerCategory; i += stride)
				{
					if (pickedBytes + sorted[i].Length > BenchMaxBytesPerCategory)
					{
						continue; // 上限超過するファイルはスキップして次の候補へ
					}
					picked.Add(sorted[i]);
					pickedBytes += sorted[i].Length;
				}
			}

			return picked;
		}

		/// <summary>
		/// 巨大ファイルのベンチ用に切り出すチャンクの数（ファイル全体から等間隔）。
		/// ディスクイメージ等は場所によって圧縮率が3%〜100%まで極端に振れるため、
		/// 点数が少ないと「たまたま踏んだ場所」で予測が大きく外れる。
		/// 約8GBのディスクイメージでの実測: 5点×6.4MB=誤差-31.8pt / 16点×2MB=誤差+9.9pt（フル圧縮基準）。
		/// </summary>
		private const int SpreadChunkCount = 16;

		/// <summary>
		/// 巨大ファイルからベンチ用サンプルを等間隔チャンクで切り出し、チャンクごとに別ファイルにする。
		/// 先頭だけだとファイル内の偏り（ディスクイメージの「先頭は実行ファイル・後半はムービー」等）で
		/// 予測が歪むため全体から拾う。1ファイルに連結すると、本来は辞書窓外で
		/// マッチしないはずの遠距離データ同士が人工的に隣接して「縮みすぎ」予測になるため、
		/// 別ファイル＋ベンチのソリッドオフ（BenchmarkAsync側）でチャンク間マッチを断つ。
		/// </summary>
		/// <returns>作成した一時チャンクファイルのパス一覧</returns>
		private static async Task<List<string>> CopySpreadChunksAsync(FileInfo source, long totalSampleBytes, CancellationToken cancellationToken)
		{
			long chunkSize = totalSampleBytes / SpreadChunkCount;
			var chunkPaths = new List<string>(SpreadChunkCount);

			await using var src = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			long fileLength = src.Length;
			var buffer = new byte[1024 * 1024];

			for (int i = 0; i < SpreadChunkCount; i++)
			{
				string chunkPath = Path.Combine(
					Path.GetTempPath(),
					$"AutoArchiver_chunk_{Guid.NewGuid():N}_{i}{source.Extension}");

				// チャンク開始位置: 先頭(0%)〜末尾(100%-chunk)を等間隔に配置
				long offset = (fileLength - chunkSize) * i / (SpreadChunkCount - 1);
				src.Seek(offset, SeekOrigin.Begin);

				await using var dst = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None);
				long remaining = chunkSize;
				while (remaining > 0)
				{
					int toRead = (int)Math.Min(buffer.Length, remaining);
					int read = await src.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
					if (read <= 0)
					{
						break;
					}
					await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
					remaining -= read;
				}
				chunkPaths.Add(chunkPath);
			}
			return chunkPaths;
		}

		/// <summary>カテゴリ別圧縮率をサイズ構成比で加重合成する</summary>
		private static double WeightedRatio(FolderAnalysis analysis, Dictionary<FileCategory, double> ratios)
		{
			double total = 0;
			foreach (FileCategory category in Enum.GetValues<FileCategory>())
			{
				long size = analysis.SizeByCategory[category];
				if (size <= 0)
				{
					continue;
				}
				// ベンチできなかったカテゴリは「縮まない」(1.0) として安全側に倒す
				double r = ratios.TryGetValue(category, out double v) ? v : 1.0;
				total += r * ((double)size / analysis.TotalSize);
			}
			return total;
		}

		private static string CategoryLabel(FileCategory category) => category switch
		{
			FileCategory.Compressed => "既圧縮",
			FileCategory.Compressible => "圧縮可能",
			FileCategory.Unknown => "不明",
			_ => category.ToString(),
		};

		/// <summary>バイト数を人間が読みやすい単位で整形する</summary>
		public static string FormatBytes(long bytes)
		{
			string[] units = { "B", "KB", "MB", "GB", "TB" };
			double value = bytes;
			int unit = 0;
			while (value >= 1024 && unit < units.Length - 1)
			{
				value /= 1024;
				unit++;
			}
			return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
		}
	}
}
