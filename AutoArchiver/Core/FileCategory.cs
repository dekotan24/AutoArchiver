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

		/// <summary>圧縮がよく効くデータ（テキスト・ソースコード・実行ファイルなど）。7z LZMA2が最強クラス</summary>
		Compressible,

		/// <summary>
		/// 無圧縮メディア（PCM音声・生ビットマップ画像）。圧縮は効くが、RARの自動メディアフィルタ
		/// （デルタ+予測変換）が7zより強い領域のため、Compressibleの「7z即決」に混ぜず実測ベンチで決める。
		/// 実測（24bit/48kHz wav 125MB）: RAR 68.62%/9秒 vs 7z素 69.19%/90秒 vs 7z+正しいdelta 68.88%/89秒
		/// </summary>
		UncompressedMedia,

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
			"mp4", "mkv", "webm", "avi", "wmv", "flv", "mov", "m4v", "mpg", "mpeg", "ts", "m2ts", "mts", "m2v", "vob", "3gp", "ogv",
			// 画像（非可逆・可逆圧縮済み）
			"jpg", "jpeg", "png", "gif", "webp", "avif", "heic", "heif", "jxl", "jp2", "j2k", "qoi",
			// 音声（圧縮コーデック。wv/tta/shnはflac同様の可逆圧縮）
			"mp3", "aac", "m4a", "ogg", "opus", "flac", "wma", "ape", "tak", "wv", "tta", "shn", "mka",
			// 書庫・圧縮ファイル
			"zip", "7z", "rar", "gz", "tgz", "bz2", "xz", "zst", "lz4", "cab", "lzh", "lha", "arj", "z01", "part1",
			// ZIPコンテナ形式のドキュメント
			"docx", "xlsx", "pptx", "odt", "ods", "odp", "epub", "xps",
			// その他ZIP/圧縮ベース（msiは内部CAB圧縮、deb/rpmも圧縮アーカイブ）
			"apk", "jar", "war", "aar", "ipa", "vsix", "nupkg", "crx", "unitypackage", "msi", "msix", "appx", "deb", "rpm",
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
			// 低圧縮・複合メディア（RARフィルタの対象外か効果が不明確なもの。生PCM/生ビットマップはUncompressedMediaへ）
			"psd", "ai", "svg", "dds", "hdr", "exr",
			// 実行ファイル・バイナリ（LZMAのBCJフィルタがよく効く）
			"exe", "dll", "sys", "ocx", "pdb", "lib", "obj", "so", "dylib", "elf", "wasm",
			// フォント（ttf/otfは非圧縮。圧縮済みのwoff/woff2とは別物）
			"ttf", "otf",
			// データベース・オフィス旧形式
			"db", "sqlite", "sqlite3", "mdb", "accdb", "doc", "xls", "ppt", "rtf",
			// 3D・ゲーム開発系テキスト
			"fbx", "gltf", "mtl", "vrm", "unity", "prefab", "asset", "meta", "shader",
			// セーブデータ・辞書等でよく縮む
			"sav", "dmp", "dump", "mdmp",
		};

		/// <summary>無圧縮メディア系の拡張子（PCM音声・生ビットマップ画像）。RARの自動メディアフィルタが効く</summary>
		private static readonly HashSet<string> UncompressedMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
		{
			// PCM音声（w64=Wave64はDAW・長時間録音系、caf=Apple無圧縮コンテナ）
			"wav", "aiff", "aif", "w64", "caf", "au", "snd",
			// 生ビットマップ・単純ラスター画像
			"bmp", "tif", "tiff", "tga", "pcx", "ppm", "pgm", "pbm", "pnm", "dib", "sgi", "ras",
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
			if (UncompressedMediaExtensions.Contains(ext))
			{
				return FileCategory.UncompressedMedia;
			}
			return FileCategory.Unknown;
		}
	}
}
