using System.IO.Pipes;
using System.Text.Json;

namespace AutoArchiver.Utils
{
	/// <summary>
	/// 多重起動の合流機構。
	/// 2つ目以降の起動は、コマンドライン引数を名前付きパイプで既存インスタンスへ送って即終了する。
	/// これにより「圧縮中に『送る』で追加投入」が並列圧縮ではなく既存キューへの追加になる。
	/// </summary>
	public static class SingleInstance
	{
		private const string MutexName = @"Local\AutoArchiver_SingleInstance";
		private const string PipeName = "AutoArchiver_ArgsPipe";

		private static Mutex? _mutex;

		/// <summary>
		/// 最初のインスタンスとしてMutexを取得する。既に起動済みならfalse。
		/// </summary>
		public static bool TryAcquire()
		{
			_mutex = new Mutex(true, MutexName, out bool createdNew);
			return createdNew;
		}

		/// <summary>
		/// 既存インスタンスへ引数を送る。送れたらtrue（呼び出し側はそのまま終了してよい）。
		/// </summary>
		public static bool SendToExistingInstance(string[] args)
		{
			try
			{
				using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
				client.Connect(3000);
				using var writer = new StreamWriter(client);
				writer.Write(JsonSerializer.Serialize(args));
				writer.Flush();
				return true;
			}
			catch
			{
				// 既存インスタンスが終了直後等で繋がらなければ、自分が通常起動する
				return false;
			}
		}

		/// <summary>
		/// 引数受信サーバーを開始する（最初のインスタンス側）。
		/// 受信するたびに onReceived が呼ばれる（ワーカースレッドから）。
		/// </summary>
		public static void StartServer(Action<string[]> onReceived)
		{
			var thread = new Thread(() =>
			{
				while (true)
				{
					try
					{
						using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
						server.WaitForConnection();
						using var reader = new StreamReader(server);
						string json = reader.ReadToEnd();
						string[]? args = JsonSerializer.Deserialize<string[]>(json);
						if (args != null)
						{
							// 空配列でも通知する（受信側でウィンドウ前面化に使う）
							onReceived(args);
						}
					}
					catch
					{
						// パイプの一時的なエラーは無視して待ち受けを続ける
					}
				}
			})
			{
				IsBackground = true,
				Name = "AutoArchiver-PipeServer",
			};
			thread.Start();
		}
	}
}
