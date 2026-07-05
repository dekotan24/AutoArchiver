namespace AutoArchiver.Core
{
	/// <summary>
	/// 出力アーカイブ形式。
	/// </summary>
	public enum ArchiveFormat
	{
		/// <summary>7z (LZMA2) 最高圧縮。テキスト・バイナリ主体で最強クラス</summary>
		SevenZip,

		/// <summary>RAR5 最高圧縮。データによっては7zを上回ることがある</summary>
		Rar,

		/// <summary>無圧縮ZIP (store)。既圧縮データ主体のとき、時間を掛けずにまとめる用</summary>
		ZipStore,
	}

	/// <summary>
	/// 形式自動選択の結果。選んだ形式と、その理由（ログ表示用）を持つ。
	/// </summary>
	public class FormatDecision
	{
		public ArchiveFormat Format { get; }

		/// <summary>選択理由（ユーザーに見せる日本語文）</summary>
		public string Reason { get; }

		/// <summary>層化ベンチを実行した場合の詳細ログ（実測圧縮率など）。無ければ空</summary>
		public List<string> BenchmarkDetails { get; } = new();

		/// <summary>
		/// 予測圧縮率（出力/入力）。ベンチ実行時とstore即決時のみ設定される。
		/// 空き容量チェックの見積もりに使う（nullなら「縮まない」前提で見積もる）。
		/// </summary>
		public double? PredictedRatio { get; set; }

		public FormatDecision(ArchiveFormat format, string reason)
		{
			Format = format;
			Reason = reason;
		}

		/// <summary>形式に対応する拡張子（ドット無し）</summary>
		public string Extension => Format switch
		{
			ArchiveFormat.SevenZip => "7z",
			ArchiveFormat.Rar => "rar",
			ArchiveFormat.ZipStore => "zip",
			_ => "7z",
		};

		/// <summary>形式の表示名</summary>
		public string DisplayName => Format switch
		{
			ArchiveFormat.SevenZip => "7z (LZMA2 最高圧縮)",
			ArchiveFormat.Rar => "RAR5 (最高圧縮)",
			ArchiveFormat.ZipStore => "ZIP (無圧縮 store)",
			_ => Format.ToString(),
		};
	}
}
