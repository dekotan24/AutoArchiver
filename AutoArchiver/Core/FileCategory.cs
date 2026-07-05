namespace AutoArchiver.Core
{
	/// <summary>
	/// ファイルの圧縮特性カテゴリ。
	/// 拡張子ベースのヒューリスティック判定で振り分ける。
	/// </summary>
	public enum FileCategory
	{
		/// <summary>既に圧縮済みのデータ（動画・画像・音声・書庫など）。再圧縮してもほぼ縮まない</summary>
		Compressed,

		/// <summary>圧縮がよく効くデータ（テキスト・ソースコード・無圧縮メディア・実行ファイルなど）</summary>
		Compressible,

		/// <summary>拡張子から判断できないデータ（ゲームの .pak / .dat / 独自形式など）。実測が必要</summary>
		Unknown,
	}

	/// <summary>
	/// 拡張子からファイルカテゴリを判定する分類器。
	/// </summary>
	public static class ExtensionClassifier
	{
		/// <summary>既圧縮系の拡張子（小文字・ドット無し）</summary>
		private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
		{
			// 動画（コンテナ内コーデックが圧縮済み）
			"mp4", "mkv", "webm", "avi", "wmv", "flv", "mov", "m4v", "mpg", "mpeg", "ts", "m2ts", "vob", "3gp",
			// 画像（非可逆・可逆圧縮済み）
			"jpg", "jpeg", "png", "gif", "webp", "avif", "heic", "heif", "jxl",
			// 音声（圧縮コーデック）
			"mp3", "aac", "m4a", "ogg", "opus", "flac", "wma", "ape", "tak",
			// 書庫・圧縮ファイル
			"zip", "7z", "rar", "gz", "tgz", "bz2", "xz", "zst", "lz4", "cab", "lzh", "lha", "arj", "z01", "part1",
			// ZIPコンテナ形式のドキュメント
			"docx", "xlsx", "pptx", "odt", "ods", "odp", "epub", "xps",
			// その他ZIP/圧縮ベース
			"apk", "jar", "war", "aar", "ipa", "vsix", "nupkg", "crx", "unitypackage",
			// フォント（内部圧縮済み）
			"woff", "woff2",
			// だいたい内部ストリームが圧縮済み
			"pdf", "swf", "mht",
		};

		/// <summary>圧縮がよく効く系の拡張子（小文字・ドット無し）</summary>
		private static readonly HashSet<string> CompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
		{
			// プレーンテキスト・ログ・データ
			"txt", "log", "csv", "tsv", "json", "xml", "yaml", "yml", "toml", "ini", "cfg", "conf", "md", "rst",
			"srt", "ass", "vtt", "lrc", "sub",
			// Web系ソース
			"html", "htm", "css", "js", "ts", "jsx", "tsx", "vue", "svelte", "php", "asp", "aspx", "jsp",
			// プログラミング言語ソース
			"c", "h", "cpp", "hpp", "cc", "cs", "vb", "java", "kt", "py", "rb", "go", "rs", "swift", "m",
			"pl", "lua", "sh", "ps1", "psm1", "bat", "cmd", "sql", "r", "scala", "dart", "hs", "asm",
			// プロジェクト・設定ファイル
			"sln", "csproj", "vbproj", "vcxproj", "props", "targets", "gradle", "pom", "make", "cmake",
			"gitignore", "gitattributes", "editorconfig", "dockerfile", "env",
			// 無圧縮・低圧縮メディア
			"wav", "aiff", "aif", "bmp", "tif", "tiff", "tga", "psd", "ai", "svg", "dds", "hdr", "exr", "pcx",
			// 実行ファイル・バイナリ（LZMAのBCJフィルタがよく効く）
			"exe", "dll", "sys", "ocx", "pdb", "lib", "obj", "so", "dylib", "elf",
			// データベース・オフィス旧形式
			"db", "sqlite", "sqlite3", "mdb", "accdb", "doc", "xls", "ppt", "rtf",
			// 3D・ゲーム開発系テキスト
			"fbx", "gltf", "mtl", "vrm", "unity", "prefab", "asset", "meta", "shader",
			// セーブデータ・辞書等でよく縮む
			"sav", "dmp", "dump", "mdmp",
		};

		/// <summary>
		/// ファイルパスからカテゴリを判定する。
		/// </summary>
		public static FileCategory Classify(string filePath)
		{
			string ext = Path.GetExtension(filePath).TrimStart('.');
			if (ext.Length == 0)
			{
				return FileCategory.Unknown;
			}
			if (CompressedExtensions.Contains(ext))
			{
				return FileCategory.Compressed;
			}
			if (CompressibleExtensions.Contains(ext))
			{
				return FileCategory.Compressible;
			}
			return FileCategory.Unknown;
		}
	}
}
