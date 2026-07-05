namespace AutoArchiver.Core
{
	/// <summary>
	/// 圧縮対象（ファイル・フォルダの集合）の分析結果。カテゴリ別のサイズ・件数と全ファイルリストを保持する。
	/// </summary>
	public class FolderAnalysis
	{
		/// <summary>カテゴリ別のファイルリスト</summary>
		public Dictionary<FileCategory, List<FileInfo>> FilesByCategory { get; } = new();

		/// <summary>カテゴリ別の合計サイズ（バイト）</summary>
		public Dictionary<FileCategory, long> SizeByCategory { get; } = new();

		/// <summary>全ファイル数</summary>
		public int TotalFileCount { get; private set; }

		/// <summary>全ファイル合計サイズ（バイト）</summary>
		public long TotalSize { get; private set; }

		/// <summary>読み取れなかったファイル・フォルダの数（アクセス拒否等）</summary>
		public int InaccessibleCount { get; set; }

		public FolderAnalysis()
		{
			foreach (FileCategory category in Enum.GetValues<FileCategory>())
			{
				FilesByCategory[category] = new List<FileInfo>();
				SizeByCategory[category] = 0;
			}
		}

		/// <summary>ファイルを1件追加して集計を更新する</summary>
		public void AddFile(FileInfo file, FileCategory category)
		{
			FilesByCategory[category].Add(file);
			SizeByCategory[category] += file.Length;
			TotalFileCount++;
			TotalSize += file.Length;
		}

		/// <summary>指定カテゴリのサイズ構成比（0.0〜1.0）。全体サイズ0のときは0を返す</summary>
		public double SizeRatio(FileCategory category)
		{
			if (TotalSize <= 0)
			{
				return 0.0;
			}
			return (double)SizeByCategory[category] / TotalSize;
		}
	}

	/// <summary>
	/// 圧縮対象（ファイル・フォルダ混在可）を走査して、カテゴリ別のサイズ構成を集計する。
	/// </summary>
	public static class FolderAnalyzer
	{
		/// <summary>
		/// 対象アイテム（ファイル・フォルダ混在可）の全ファイルを分類・集計する。
		/// フォルダは再帰スキャン。アクセスできない項目はスキップしてカウントだけ残す。
		/// </summary>
		public static FolderAnalysis Analyze(IReadOnlyList<string> paths, CancellationToken cancellationToken)
		{
			var analysis = new FolderAnalysis();
			foreach (string path in paths)
			{
				if (Directory.Exists(path))
				{
					ScanDirectory(new DirectoryInfo(path), analysis, cancellationToken);
				}
				else if (File.Exists(path))
				{
					var file = new FileInfo(path);
					analysis.AddFile(file, ExtensionClassifier.Classify(file.Name));
				}
				else
				{
					analysis.InaccessibleCount++;
				}
			}
			return analysis;
		}

		private static void ScanDirectory(DirectoryInfo dir, FolderAnalysis analysis, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();

			FileInfo[] files;
			DirectoryInfo[] subDirs;
			try
			{
				files = dir.GetFiles();
				subDirs = dir.GetDirectories();
			}
			catch (UnauthorizedAccessException)
			{
				analysis.InaccessibleCount++;
				return;
			}
			catch (IOException)
			{
				analysis.InaccessibleCount++;
				return;
			}

			foreach (var file in files)
			{
				// リパースポイント（シンボリックリンク等）は実体を二重カウントしないようスキップ
				if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
				{
					continue;
				}
				analysis.AddFile(file, ExtensionClassifier.Classify(file.Name));
			}

			foreach (var subDir in subDirs)
			{
				if ((subDir.Attributes & FileAttributes.ReparsePoint) != 0)
				{
					continue;
				}
				ScanDirectory(subDir, analysis, cancellationToken);
			}
		}
	}
}
