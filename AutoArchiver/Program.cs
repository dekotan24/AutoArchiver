using System.Runtime.InteropServices;

namespace AutoArchiver
{
	internal static class Program
	{
		/// <summary>
		/// アプリケーションのエントリポイント。
		/// コマンドライン引数（「送る」メニューやD&Dで渡されたフォルダパス）をそのままMainFormへ渡す。
		/// </summary>
		[STAThread]
		static void Main(string[] args)
		{
			// 多重起動は既存インスタンスへ引数を渡して合流する（並列圧縮によるCPU取り合いを防ぐ）。
			// 引数なしでも空配列を送り、既存ウィンドウの前面化だけ行う。
			if (!Utils.SingleInstance.TryAcquire())
			{
				if (Utils.SingleInstance.SendToExistingInstance(args))
				{
					return;
				}
				// 送信失敗時（既存側が終了直後だった場合など）はそのまま起動する
			}

			EnableDarkMode();
			ApplicationConfiguration.Initialize();
			Application.Run(new Forms.MainForm(args));
		}

		/// <summary>
		/// Windows 10でタイトルバーのダークモードを有効にする。
		/// DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)だけでは反映されないため、
		/// uxthemeの SetPreferredAppMode(ForceDark) をウィンドウ生成前に呼ぶ（Win10 1903+の定番手法）。
		/// </summary>
		private static void EnableDarkMode()
		{
			try
			{
				SetPreferredAppMode(2); // 2 = ForceDark
			}
			catch
			{
				// undocumented APIのため、存在しない環境では何もしない
			}
		}

		[DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
		private static extern int SetPreferredAppMode(int preferredAppMode);
	}
}
