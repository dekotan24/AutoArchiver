namespace AutoArchiver.Core
{
	/// <summary>
	/// 圧縮実行時のオプション（UI層の設定から組み立ててエンジンへ渡す）。
	/// </summary>
	public class CompressionOptions
	{
		/// <summary>書庫パスワード。null・空なら暗号化なし</summary>
		public string? Password { get; init; }

		/// <summary>RAR形式のリカバリレコード（%）。0なら付けない。RAR以外の形式では無視される</summary>
		public int RecoveryRecordPercent { get; init; }

		/// <summary>書庫から除外するファイル名パターン（ワイルドカード可）</summary>
		public IReadOnlyList<string> ExcludePatterns { get; init; } = Array.Empty<string>();

		/// <summary>すべて既定値（暗号化なし・RRなし・除外なし）</summary>
		public static readonly CompressionOptions Default = new();

		/// <summary>パスワードが設定されているか</summary>
		public bool HasPassword => !string.IsNullOrEmpty(Password);
	}
}
