using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoArchiver.Utils
{
	/// <summary>圧縮成功後の元アイテムの扱い</summary>
	public enum SourceDeleteMode
	{
		/// <summary>毎回ダイアログで確認する</summary>
		AskEveryTime = 0,

		/// <summary>常に残す（確認なし）</summary>
		AlwaysKeep = 1,

		/// <summary>常にゴミ箱へ移動（確認なし）</summary>
		AlwaysRecycle = 2,
	}

	/// <summary>
	/// アプリ設定。%APPDATA%\AutoArchiver\settings.json に保存する。
	/// 「送る」メニュー経由の起動時もこの設定値が使われる。
	/// </summary>
	public class AppSettings
	{
		/// <summary>
		/// 圧縮ファイルの保存先フォルダ。空文字なら既定（元フォルダと同じ親ディレクトリ）。
		/// </summary>
		public string OutputDirectory { get; set; } = "";

		/// <summary>
		/// バッチモード。trueなら投入した1ファイル/フォルダごとに1書庫、falseなら一度の投入をまとめて1書庫。
		/// </summary>
		public bool BatchMode { get; set; } = false;

		/// <summary>圧縮成功後の元アイテムの扱い</summary>
		public SourceDeleteMode DeleteMode { get; set; } = SourceDeleteMode.AskEveryTime;

		/// <summary>全キュー完了時に通知（音+バルーン）を出す</summary>
		public bool NotifyOnComplete { get; set; } = true;

		/// <summary>書庫から除外するファイル名パターン（ワイルドカード可）</summary>
		public string[] ExcludePatterns { get; set; } = { "Thumbs.db", "desktop.ini", ".DS_Store" };

		/// <summary>RAR形式選択時のリカバリレコード（%）。0なら付けない</summary>
		public int RecoveryRecordPercent { get; set; } = 0;

		/// <summary>書庫パスワード（DPAPIで暗号化してBase64保存）。空なら暗号化なし</summary>
		public string PasswordEncrypted { get; set; } = "";

		/// <summary>復号済みパスワード。空文字ならパスワードなし</summary>
		[JsonIgnore]
		public string Password
		{
			get
			{
				if (string.IsNullOrEmpty(PasswordEncrypted))
				{
					return "";
				}
				try
				{
					byte[] cipher = Convert.FromBase64String(PasswordEncrypted);
					byte[] plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
					return Encoding.UTF8.GetString(plain);
				}
				catch
				{
					// 別ユーザー・別マシンの設定ファイル等で復号できない場合はパスワードなし扱い
					return "";
				}
			}
			set
			{
				if (string.IsNullOrEmpty(value))
				{
					PasswordEncrypted = "";
					return;
				}
				byte[] plain = Encoding.UTF8.GetBytes(value);
				byte[] cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
				PasswordEncrypted = Convert.ToBase64String(cipher);
			}
		}

		private static string SettingsPath
		{
			get
			{
				string dir = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
					"AutoArchiver");
				return Path.Combine(dir, "settings.json");
			}
		}

		/// <summary>設定を読み込む。ファイルが無い・壊れているときは既定値を返す</summary>
		public static AppSettings Load()
		{
			try
			{
				if (File.Exists(SettingsPath))
				{
					string json = File.ReadAllText(SettingsPath);
					return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
				}
			}
			catch
			{
				// 壊れた設定ファイルは既定値で上書き運用（致命的でない）
			}
			return new AppSettings();
		}

		/// <summary>設定を保存する</summary>
		public void Save()
		{
			try
			{
				string dir = Path.GetDirectoryName(SettingsPath)!;
				Directory.CreateDirectory(dir);
				string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
				File.WriteAllText(SettingsPath, json);
			}
			catch
			{
				// 保存失敗は動作に致命的でないため無視（次回起動時に既定値に戻るだけ）
			}
		}
	}
}
