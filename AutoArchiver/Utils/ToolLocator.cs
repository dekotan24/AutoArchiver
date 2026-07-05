namespace AutoArchiver.Utils
{
	/// <summary>
	/// 外部圧縮ツール（7z.exe / Rar.exe）の実行ファイルを検出する。
	/// </summary>
	public static class ToolLocator
	{
		private static string? _sevenZipPath;
		private static string? _rarPath;
		private static bool _searched;

		/// <summary>7z.exe のフルパス。見つからなければ null</summary>
		public static string? SevenZipPath
		{
			get
			{
				EnsureSearched();
				return _sevenZipPath;
			}
		}

		/// <summary>Rar.exe（WinRARのコンソール版）のフルパス。見つからなければ null</summary>
		public static string? RarPath
		{
			get
			{
				EnsureSearched();
				return _rarPath;
			}
		}

		/// <summary>7-Zipが利用可能か（必須ツール）</summary>
		public static bool HasSevenZip => SevenZipPath != null;

		/// <summary>WinRARが利用可能か（あればrar形式も候補に入る）</summary>
		public static bool HasRar => RarPath != null;

		private static void EnsureSearched()
		{
			if (_searched)
			{
				return;
			}
			_searched = true;

			_sevenZipPath = FindFirstExisting(new[]
			{
				@"C:\Program Files\7-Zip\7z.exe",
				@"C:\Program Files (x86)\7-Zip\7z.exe",
			}) ?? FindOnPath("7z.exe");

			_rarPath = FindFirstExisting(new[]
			{
				@"C:\Program Files\WinRAR\Rar.exe",
				@"C:\Program Files (x86)\WinRAR\Rar.exe",
			}) ?? FindOnPath("Rar.exe");
		}

		private static string? FindFirstExisting(string[] candidates)
		{
			foreach (string path in candidates)
			{
				if (File.Exists(path))
				{
					return path;
				}
			}
			return null;
		}

		/// <summary>環境変数PATHから実行ファイルを探す</summary>
		private static string? FindOnPath(string exeName)
		{
			string? pathEnv = Environment.GetEnvironmentVariable("PATH");
			if (string.IsNullOrEmpty(pathEnv))
			{
				return null;
			}
			foreach (string dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				try
				{
					string candidate = Path.Combine(dir.Trim(), exeName);
					if (File.Exists(candidate))
					{
						return candidate;
					}
				}
				catch (ArgumentException)
				{
					// PATH内の不正な文字を含むエントリは無視
				}
			}
			return null;
		}
	}
}
