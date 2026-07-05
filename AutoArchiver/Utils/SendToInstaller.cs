using System.Runtime.InteropServices;

namespace AutoArchiver.Utils
{
	/// <summary>
	/// エクスプローラーの「送る」メニューへのショートカット登録・解除。
	/// 通常（まとめて1書庫）とバッチ（1アイテム=1書庫）の2つを登録する。
	/// WScript.Shell COMを遅延バインディングで使い、参照追加なしで .lnk を作成する。
	/// </summary>
	public static class SendToInstaller
	{
		private const string ShortcutName = "AutoArchiver (自動圧縮).lnk";
		private const string BatchShortcutName = "AutoArchiver (個別に自動圧縮).lnk";

		/// <summary>バッチモード起動を示すコマンドライン引数</summary>
		public const string BatchArgument = "--batch";

		private static string SendToDir => Environment.GetFolderPath(Environment.SpecialFolder.SendTo);

		private static string ShortcutPath => Path.Combine(SendToDir, ShortcutName);
		private static string BatchShortcutPath => Path.Combine(SendToDir, BatchShortcutName);

		/// <summary>登録済みかどうか（どちらか一方でも存在すればtrue）</summary>
		public static bool IsInstalled => File.Exists(ShortcutPath) || File.Exists(BatchShortcutPath);

		/// <summary>「送る」メニューにショートカット2種（通常/バッチ）を作成する</summary>
		public static void Install()
		{
			CreateShortcut(ShortcutPath, "", "選択したファイル・フォルダをまとめて1書庫に自動圧縮");
			CreateShortcut(BatchShortcutPath, BatchArgument, "選択したファイル・フォルダを1つずつ個別の書庫に自動圧縮");
		}

		/// <summary>「送る」メニューからショートカットを削除する</summary>
		public static void Uninstall()
		{
			if (File.Exists(ShortcutPath))
			{
				File.Delete(ShortcutPath);
			}
			if (File.Exists(BatchShortcutPath))
			{
				File.Delete(BatchShortcutPath);
			}
		}

		private static void CreateShortcut(string lnkPath, string arguments, string description)
		{
			string exePath = Application.ExecutablePath;

			Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
			if (shellType == null)
			{
				throw new InvalidOperationException("WScript.Shell を利用できません。");
			}

			object? shell = Activator.CreateInstance(shellType);
			if (shell == null)
			{
				throw new InvalidOperationException("WScript.Shell の生成に失敗しました。");
			}

			try
			{
				object? shortcut = shellType.InvokeMember(
					"CreateShortcut",
					System.Reflection.BindingFlags.InvokeMethod,
					null, shell, new object[] { lnkPath });
				if (shortcut == null)
				{
					throw new InvalidOperationException("ショートカットの作成に失敗しました。");
				}

				Type shortcutType = shortcut.GetType();
				shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
				shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
				shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exePath) ?? "" });
				shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { description });
				shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);

				Marshal.ReleaseComObject(shortcut);
			}
			finally
			{
				Marshal.ReleaseComObject(shell);
			}
		}
	}
}
