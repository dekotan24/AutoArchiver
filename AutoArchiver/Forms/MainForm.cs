using System.Runtime.InteropServices;
using AutoArchiver.Core;
using AutoArchiver.Utils;

namespace AutoArchiver.Forms
{
	/// <summary>
	/// メインウィンドウ。
	/// フォルダのD&D／「送る」メニュー経由の起動引数を受け取り、
	/// キューに積んで1件ずつ 分析→形式決定→圧縮→書庫テスト を実行する。
	/// </summary>
	public class MainForm : Form
	{
		// ---- ダークテーマ配色 ----
		private static readonly Color ColorBack = Color.FromArgb(30, 30, 30);
		private static readonly Color ColorPanel = Color.FromArgb(37, 37, 38);
		private static readonly Color ColorText = Color.FromArgb(230, 230, 230);
		private static readonly Color ColorTextSub = Color.FromArgb(150, 150, 150);
		private static readonly Color ColorAccent = Color.FromArgb(86, 156, 214);
		private static readonly Color ColorSuccess = Color.FromArgb(106, 200, 120);
		private static readonly Color ColorError = Color.FromArgb(240, 100, 100);
		private static readonly Color ColorWarn = Color.FromArgb(220, 180, 90);
		private static readonly Color ColorBorder = Color.FromArgb(70, 70, 72);

		// ---- UIコントロール ----
		private readonly Panel _dropZone = new();
		private readonly Label _dropLabel = new();
		private readonly Label _lblFolder = new();
		private readonly Label _lblPhase = new();
		private readonly FlatProgressBar _progressBar = new();
		private readonly RichTextBox _logBox = new();
		private readonly TextBox _outputDirBox = new();
		private readonly Button _btnBrowseOutput = new();
		private readonly CheckBox _chkBatch = new();
		private readonly Button _btnSendTo = new();
		private readonly Button _btnSettings = new();
		private readonly Button _btnCancel = new();
		private readonly Label _lblQueue = new();
		private readonly NotifyIcon _notifyIcon = new();

		// ---- 処理状態 ----
		private readonly AppSettings _settings = AppSettings.Load();

		/// <summary>ジョブキュー。1エントリ（アイテムの集合）= 1書庫</summary>
		private readonly Queue<List<string>> _queue = new();
		private bool _processing;
		private CancellationTokenSource? _cts;

		public MainForm(string[] args)
		{
			InitializeLayout();

			// 通知アイコン（バルーン通知用。アプリ起動中のみ表示）
			try
			{
				_notifyIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
			}
			catch
			{
				_notifyIcon.Icon = SystemIcons.Application;
			}
			_notifyIcon.Text = "AutoArchiver";
			_notifyIcon.Visible = true;
			_notifyIcon.DoubleClick += (_, _) => BringToFront2();
			FormClosed += (_, _) => _notifyIcon.Dispose();

			// 多重起動の合流: 2つ目以降のインスタンスから引数がパイプで届く
			SingleInstance.StartServer(receivedArgs => SafeInvoke(() =>
			{
				BringToFront2();
				HandleStartupArgs(receivedArgs);
			}));

			// 起動引数（「送る」メニュー等）のファイル・フォルダをキューへ。
			// --batch 付き起動は1アイテム=1書庫、無しはまとめて1書庫。
			Shown += (_, _) =>
			{
				CheckTools();
				UpdateSendToButton();
				HandleStartupArgs(args);
			};
		}

		/// <summary>起動引数（またはパイプ受信引数）を解析してキューへ投入する</summary>
		private void HandleStartupArgs(string[] args)
		{
			var paths = new List<string>();
			bool batchFromArgs = false;
			foreach (string arg in args)
			{
				if (string.Equals(arg, SendToInstaller.BatchArgument, StringComparison.OrdinalIgnoreCase))
				{
					batchFromArgs = true;
				}
				else
				{
					paths.Add(arg);
				}
			}
			if (paths.Count > 0)
			{
				EnqueueItems(paths, batchFromArgs);
			}
		}

		/// <summary>最小化・背面のウィンドウを前面に出す</summary>
		private void BringToFront2()
		{
			if (WindowState == FormWindowState.Minimized)
			{
				WindowState = FormWindowState.Normal;
			}
			Activate();
		}

		// ================================================================
		// UI構築
		// ================================================================

		private void InitializeLayout()
		{
			Text = "AutoArchiver";
			// exeに埋め込んだアイコンをタイトルバー・タスクバーにも反映
			try
			{
				Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
			}
			catch
			{
				// アイコン取得失敗は既定アイコンのままでよい
			}
			Size = new Size(680, 620);
			MinimumSize = new Size(520, 420);
			BackColor = ColorBack;
			ForeColor = ColorText;
			Font = new Font("Yu Gothic UI", 9.5f);
			StartPosition = FormStartPosition.CenterScreen;
			AllowDrop = true;

			DragEnter += OnDragEnter;
			DragDrop += OnDragDrop;
			FormClosing += OnFormClosing;

			// --- 下部バー（先にDockさせる） ---
			var bottomPanel = new Panel
			{
				Dock = DockStyle.Bottom,
				Height = 52,
				BackColor = ColorPanel,
				Padding = new Padding(12, 10, 12, 10),
			};

			StyleButton(_btnSendTo);
			_btnSendTo.Width = 190;
			_btnSendTo.Dock = DockStyle.Left;
			_btnSendTo.Click += OnSendToButtonClick;

			StyleButton(_btnSettings);
			_btnSettings.Text = "設定…";
			_btnSettings.Width = 80;
			_btnSettings.Dock = DockStyle.Left;
			_btnSettings.Click += (_, _) =>
			{
				using var dialog = new SettingsForm(_settings);
				dialog.ShowDialog(this);
			};

			_chkBatch.Text = "バッチ（1アイテム=1書庫）";
			_chkBatch.Width = 230;
			_chkBatch.Dock = DockStyle.Left;
			_chkBatch.Padding = new Padding(12, 0, 0, 0);
			_chkBatch.ForeColor = ColorText;
			_chkBatch.Checked = _settings.BatchMode;
			_chkBatch.CheckedChanged += (_, _) => _settings.BatchMode = _chkBatch.Checked;

			StyleButton(_btnCancel);
			_btnCancel.Text = "キャンセル";
			_btnCancel.Width = 110;
			_btnCancel.Dock = DockStyle.Right;
			_btnCancel.Enabled = false;
			_btnCancel.Click += (_, _) => _cts?.Cancel();

			_lblQueue.Dock = DockStyle.Fill;
			_lblQueue.TextAlign = ContentAlignment.MiddleCenter;
			_lblQueue.ForeColor = ColorTextSub;
			_lblQueue.Text = "";

			bottomPanel.Controls.Add(_lblQueue);
			bottomPanel.Controls.Add(_chkBatch);
			bottomPanel.Controls.Add(_btnSettings);
			bottomPanel.Controls.Add(_btnSendTo);
			bottomPanel.Controls.Add(_btnCancel);

			// --- 保存先指定バー ---
			var outputPanel = new Panel
			{
				Dock = DockStyle.Bottom,
				Height = 42,
				BackColor = ColorPanel,
				Padding = new Padding(12, 8, 12, 8),
			};

			var lblOutput = new Label
			{
				Text = "保存先:",
				Dock = DockStyle.Left,
				Width = 60,
				TextAlign = ContentAlignment.MiddleLeft,
				ForeColor = ColorTextSub,
			};

			StyleButton(_btnBrowseOutput);
			_btnBrowseOutput.Text = "参照…";
			_btnBrowseOutput.Width = 80;
			_btnBrowseOutput.Dock = DockStyle.Right;
			_btnBrowseOutput.Click += OnBrowseOutputClick;

			_outputDirBox.Dock = DockStyle.Fill;
			_outputDirBox.BorderStyle = BorderStyle.FixedSingle;
			_outputDirBox.BackColor = Color.FromArgb(45, 45, 48);
			_outputDirBox.ForeColor = ColorText;
			_outputDirBox.PlaceholderText = "（未指定なら元フォルダと同じ場所に保存）";
			_outputDirBox.Text = _settings.OutputDirectory;
			_outputDirBox.TextChanged += (_, _) => _settings.OutputDirectory = _outputDirBox.Text.Trim();

			// TextBoxを縦中央に寄せるための入れ子パネル
			var outputBoxHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 2, 8, 0) };
			outputBoxHost.Controls.Add(_outputDirBox);

			outputPanel.Controls.Add(outputBoxHost);
			outputPanel.Controls.Add(lblOutput);
			outputPanel.Controls.Add(_btnBrowseOutput);

			// --- ドロップゾーン ---
			_dropZone.Dock = DockStyle.Top;
			_dropZone.Height = 110;
			_dropZone.BackColor = ColorBack;
			_dropZone.Padding = new Padding(14);
			_dropZone.Paint += OnDropZonePaint;
			_dropZone.Cursor = Cursors.Hand;
			_dropZone.Click += OnDropZoneClick;

			_dropLabel.Dock = DockStyle.Fill;
			_dropLabel.TextAlign = ContentAlignment.MiddleCenter;
			_dropLabel.Text = "📁 ファイル・フォルダをここにドロップ / クリックで選択\r\n（複数可 / 形式は自動選択・最高圧縮・書庫テストまで自動）";
			_dropLabel.ForeColor = ColorTextSub;
			_dropLabel.Cursor = Cursors.Hand;
			_dropLabel.Click += OnDropZoneClick;
			// ラベルへのドロップもフォームと同じ扱いにする
			_dropLabel.AllowDrop = true;
			_dropLabel.DragEnter += OnDragEnter;
			_dropLabel.DragDrop += OnDragDrop;
			_dropZone.Controls.Add(_dropLabel);

			// --- 現在の処理表示 ---
			var currentPanel = new Panel
			{
				Dock = DockStyle.Top,
				Height = 96,
				BackColor = ColorPanel,
				Padding = new Padding(14, 8, 14, 8),
			};

			_lblFolder.Dock = DockStyle.Top;
			_lblFolder.Height = 26;
			_lblFolder.Font = new Font("Yu Gothic UI", 10.5f, FontStyle.Bold);
			_lblFolder.ForeColor = ColorText;
			_lblFolder.Text = "待機中";

			_lblPhase.Dock = DockStyle.Top;
			_lblPhase.Height = 24;
			_lblPhase.ForeColor = ColorTextSub;
			_lblPhase.Text = "フォルダを投入すると自動で処理を始めます";

			_progressBar.Dock = DockStyle.Top;
			_progressBar.Height = 14;

			currentPanel.Controls.Add(_progressBar);
			currentPanel.Controls.Add(_lblPhase);
			currentPanel.Controls.Add(_lblFolder);

			// --- ログ ---
			_logBox.Dock = DockStyle.Fill;
			_logBox.ReadOnly = true;
			_logBox.BorderStyle = BorderStyle.None;
			_logBox.BackColor = ColorBack;
			_logBox.ForeColor = ColorText;
			_logBox.Font = new Font("Consolas", 9.5f);
			_logBox.ScrollBars = RichTextBoxScrollBars.Vertical;

			Controls.Add(_logBox);
			Controls.Add(currentPanel);
			Controls.Add(_dropZone);
			Controls.Add(outputPanel);
			Controls.Add(bottomPanel);

			HandleCreated += (_, _) =>
			{
				// タイトルバーのダーク化はハンドル生成後でないと効かない
				ApplyDarkTitleBar();
				// ログのスクロールバーもダーク化（Explorerのダークテーマを流用）
				SetWindowTheme(_logBox.Handle, "DarkMode_Explorer", null);
			};
		}

		private void StyleButton(Button button)
		{
			button.FlatStyle = FlatStyle.Flat;
			button.FlatAppearance.BorderColor = ColorBorder;
			button.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 55, 58);
			button.BackColor = Color.FromArgb(45, 45, 48);
			button.ForeColor = ColorText;
			button.Height = 32;
		}

		/// <summary>ドロップゾーンの点線枠を描画する</summary>
		private void OnDropZonePaint(object? sender, PaintEventArgs e)
		{
			using var pen = new Pen(ColorBorder, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
			var rect = _dropZone.ClientRectangle;
			rect.Inflate(-8, -8);
			e.Graphics.DrawRectangle(pen, rect);
		}

		/// <summary>Windows 10/11 のダークタイトルバーを適用する</summary>
		private void ApplyDarkTitleBar()
		{
			try
			{
				int enabled = 1;
				// 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（Win10 20H1以降）。失敗したら旧ビルド用の19を試す
				if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) != 0)
				{
					DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
				}
			}
			catch
			{
				// 古いOSでは失敗しても実害なし
			}
		}

		// ================================================================
		// D&D・キュー投入
		// ================================================================

		/// <summary>ドロップゾーンのクリックでフォルダ選択ダイアログを開く（ファイル・複数はD&amp;Dで対応）</summary>
		private void OnDropZoneClick(object? sender, EventArgs e)
		{
			using var dialog = new FolderBrowserDialog
			{
				Description = "圧縮するフォルダを選択",
				UseDescriptionForTitle = true,
				ShowNewFolderButton = false,
			};

			if (dialog.ShowDialog(this) != DialogResult.OK)
			{
				return;
			}
			if (Directory.Exists(dialog.SelectedPath))
			{
				EnqueueItems(new[] { dialog.SelectedPath }, _chkBatch.Checked);
			}
		}

		private void OnDragEnter(object? sender, DragEventArgs e)
		{
			if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
			{
				e.Effect = DragDropEffects.Copy;
			}
		}

		private void OnDragDrop(object? sender, DragEventArgs e)
		{
			if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths)
			{
				return;
			}
			EnqueueItems(paths, _chkBatch.Checked);
		}

		/// <summary>
		/// 投入されたファイル・フォルダをジョブとしてキューに積む。
		/// batchがtrueなら1アイテム=1書庫、falseなら全アイテムをまとめて1書庫。
		/// </summary>
		private void EnqueueItems(IEnumerable<string> paths, bool batch)
		{
			var valid = new List<string>();
			foreach (string path in paths)
			{
				// ドライブルート（C:\ 等）は名前が取れずパス組み立てが破綻するため対象外
				string? root = Path.GetPathRoot(path);
				if (root != null && string.Equals(
						root.TrimEnd('\\', '/'),
						path.TrimEnd('\\', '/'),
						StringComparison.OrdinalIgnoreCase))
				{
					AppendLog($"⚠ ドライブ全体は対象にできません: {path}", ColorWarn);
					continue;
				}
				if (Directory.Exists(path) || File.Exists(path))
				{
					valid.Add(path);
				}
				else
				{
					AppendLog($"⚠ 見つかりません: {path}", ColorWarn);
				}
			}

			if (valid.Count == 0)
			{
				return;
			}

			if (batch)
			{
				// バッチモード: 1アイテム = 1書庫
				foreach (string item in valid)
				{
					_queue.Enqueue(new List<string> { item });
					AppendLog($"キューに追加: {item}", ColorAccent);
				}
			}
			else
			{
				// 通常モード: 一度の投入をまとめて1書庫
				_queue.Enqueue(valid);
				AppendLog(valid.Count == 1
					? $"キューに追加: {valid[0]}"
					: $"キューに追加: {valid.Count}アイテム（まとめて1書庫）", ColorAccent);
			}

			UpdateQueueLabel();
			_ = ProcessQueueAsync();
		}

		// ================================================================
		// キュー処理本体
		// ================================================================

		private async Task ProcessQueueAsync()
		{
			if (_processing)
			{
				return; // 既に処理ループが走っている
			}
			_processing = true;
			_btnCancel.Enabled = true;

			try
			{
				while (_queue.Count > 0)
				{
					List<string> items = _queue.Dequeue();
					UpdateQueueLabel();

					_cts = new CancellationTokenSource();

					var options = new CompressionOptions
					{
						Password = _settings.Password,
						RecoveryRecordPercent = _settings.RecoveryRecordPercent,
						ExcludePatterns = _settings.ExcludePatterns,
					};

					var job = new CompressionJob(
						items,
						_settings.OutputDirectory,
						options,
						message => SafeInvoke(() => AppendLog(message, PickLogColor(message))),
						progress => SafeInvoke(() => UpdateProgress(progress)));

					_lblFolder.Text = job.DisplayName;
					AppendLog("", ColorText);
					AppendLog($"══════ {job.DisplayName} ══════", ColorText);

					JobResult result;
					try
					{
						result = await job.RunAsync(_cts.Token);
					}
					catch (OperationCanceledException)
					{
						AppendLog("処理を中断しました。残りのキューもクリアします。", ColorWarn);
						_queue.Clear();
						UpdateQueueLabel();
						break;
					}

					if (result.Success)
					{
						double ratio = result.OriginalSize > 0 ? (double)result.ArchiveSize / result.OriginalSize : 0;
						AppendLog($"✅ 完了 ({result.Elapsed:hh\\:mm\\:ss}): {result.ArchivePath}", ColorSuccess);
						AppendLog($"   {FormatSelector.FormatBytes(result.OriginalSize)} → {FormatSelector.FormatBytes(result.ArchiveSize)} （{ratio:P1}）", ColorSuccess);

						// 元アイテムの扱いは毎回確認（ゴミ箱へ移動 or 残す）
						ConfirmAndRecycleSource(items, result);
					}
					else
					{
						AppendLog($"❌ {job.DisplayName}: {result.ErrorMessage}", ColorError);
					}
				}
			}
			finally
			{
				_processing = false;
				_btnCancel.Enabled = false;
				_cts?.Dispose();
				_cts = null;
				_lblFolder.Text = "待機中";
				_lblPhase.Text = "フォルダを投入すると自動で処理を始めます";
				_progressBar.Value = 0;
				TaskbarProgress.Clear(Handle);
				UpdateQueueLabel();
				NotifyAllDone();
			}
		}

		/// <summary>
		/// 圧縮・テスト成功後の元アイテムの扱い。
		/// 設定に応じて 毎回確認 / 常に残す / 常にゴミ箱 を実行する。
		/// </summary>
		private void ConfirmAndRecycleSource(List<string> items, JobResult result)
		{
			string targetLabel = items.Count == 1 ? "元のファイル・フォルダ" : $"元のアイテム（{items.Count}件）";

			bool recycle;
			switch (_settings.DeleteMode)
			{
				case SourceDeleteMode.AlwaysKeep:
					AppendLog($"{targetLabel}は設定に従い残しました。", ColorTextSub);
					return;

				case SourceDeleteMode.AlwaysRecycle:
					recycle = true;
					break;

				default:
					// 確認ダイアログに出すアイテム一覧（多いときは先頭だけ）
					const int maxListed = 5;
					var listed = new List<string>();
					for (int i = 0; i < items.Count && i < maxListed; i++)
					{
						listed.Add(items[i]);
					}
					string itemList = string.Join("\n", listed);
					if (items.Count > maxListed)
					{
						itemList += $"\n… 他 {items.Count - maxListed} 件";
					}

					string message =
						$"圧縮と書庫テストが完了しました。\n\n" +
						$"書庫: {Path.GetFileName(result.ArchivePath)}\n" +
						$"サイズ: {FormatSelector.FormatBytes(result.OriginalSize)} → {FormatSelector.FormatBytes(result.ArchiveSize)}\n\n" +
						$"{targetLabel}をゴミ箱へ移動しますか？\n{itemList}";

					DialogResult answer = MessageBox.Show(
						this, message, "AutoArchiver - 元アイテムの処理",
						MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
					recycle = answer == DialogResult.Yes;
					break;
			}

			if (recycle)
			{
				if (RecycleBin.MoveToRecycleBin(items))
				{
					AppendLog($"🗑 {targetLabel}をゴミ箱へ移動しました。", ColorTextSub);
				}
				else
				{
					AppendLog($"⚠ ゴミ箱への移動に失敗しました（使用中の可能性）。", ColorWarn);
				}
			}
			else
			{
				AppendLog($"{targetLabel}はそのまま残しました。", ColorTextSub);
			}
		}

		/// <summary>全キュー完了時の通知（音 + バルーン）。設定でオフにできる</summary>
		private void NotifyAllDone()
		{
			if (!_settings.NotifyOnComplete)
			{
				return;
			}
			try
			{
				System.Media.SystemSounds.Asterisk.Play();
				_notifyIcon.BalloonTipTitle = "AutoArchiver";
				_notifyIcon.BalloonTipText = "すべての圧縮が完了しました。";
				_notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
				_notifyIcon.ShowBalloonTip(5000);
			}
			catch
			{
				// 通知失敗は無害
			}
		}

		private void UpdateProgress(JobProgress progress)
		{
			string phaseText = progress.Phase switch
			{
				JobPhase.Analyzing => "① 分析中（ファイル構成のスキャン）",
				JobPhase.Benchmarking => "② 形式選択中（必要ならサンプル実測ベンチ）",
				JobPhase.Compressing => "③ 圧縮中",
				JobPhase.Testing => "④ 書庫テスト中",
				JobPhase.Done => "✅ 完了",
				_ => "",
			};

			if (progress.Percent >= 0)
			{
				phaseText += $"  {progress.Percent}%";
				_progressBar.Value = progress.Percent;
			}
			else
			{
				_progressBar.Value = 0;
			}
			_lblPhase.Text = phaseText;

			// タスクバーのアイコンにも進捗を出す（最小化中でも見えるように）
			if (progress.Phase == JobPhase.Compressing || progress.Phase == JobPhase.Testing)
			{
				TaskbarProgress.SetValue(Handle, Math.Max(0, progress.Percent), 100);
			}
			else if (progress.Phase == JobPhase.Done)
			{
				TaskbarProgress.Clear(Handle);
			}
		}

		/// <summary>保存先の「参照…」ボタン</summary>
		private void OnBrowseOutputClick(object? sender, EventArgs e)
		{
			using var dialog = new FolderBrowserDialog
			{
				Description = "圧縮ファイルの保存先フォルダを選択",
				UseDescriptionForTitle = true,
				ShowNewFolderButton = true,
			};
			if (Directory.Exists(_outputDirBox.Text.Trim()))
			{
				dialog.InitialDirectory = _outputDirBox.Text.Trim();
			}

			if (dialog.ShowDialog(this) == DialogResult.OK)
			{
				_outputDirBox.Text = dialog.SelectedPath;
				_settings.Save();
			}
		}

		private void OnFormClosing(object? sender, FormClosingEventArgs e)
		{
			_settings.Save();

			if (_processing)
			{
				DialogResult answer = MessageBox.Show(
					this,
					"圧縮処理が実行中です。中断して終了しますか？\n（作りかけの書庫は削除されます）",
					"AutoArchiver",
					MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
				if (answer == DialogResult.No)
				{
					e.Cancel = true;
					return;
				}
				_cts?.Cancel();
			}
		}

		// ================================================================
		// 「送る」メニュー登録
		// ================================================================

		private void OnSendToButtonClick(object? sender, EventArgs e)
		{
			try
			{
				if (SendToInstaller.IsInstalled)
				{
					SendToInstaller.Uninstall();
					AppendLog("「送る」メニューから登録解除しました。", ColorTextSub);
				}
				else
				{
					SendToInstaller.Install();
					AppendLog("「送る」メニューに2種類登録しました。", ColorSuccess);
					AppendLog("  ・AutoArchiver (自動圧縮) — 選択したものをまとめて1書庫", ColorTextSub);
					AppendLog("  ・AutoArchiver (個別に自動圧縮) — 1ファイル/フォルダごとに1書庫", ColorTextSub);
				}
			}
			catch (Exception ex)
			{
				AppendLog($"⚠ 「送る」メニューの更新に失敗: {ex.Message}", ColorError);
			}
			UpdateSendToButton();
		}

		private void UpdateSendToButton()
		{
			_btnSendTo.Text = SendToInstaller.IsInstalled
				? "「送る」メニューから登録解除"
				: "「送る」メニューに登録";
		}

		// ================================================================
		// ツール確認・ログ・ユーティリティ
		// ================================================================

		private void CheckTools()
		{
			if (ToolLocator.HasSevenZip)
			{
				AppendLog($"7-Zip: {ToolLocator.SevenZipPath}", ColorTextSub);
			}
			else
			{
				AppendLog("❌ 7-Zip (7z.exe) が見つかりません。https://www.7-zip.org/ からインストールしてください。", ColorError);
			}

			if (ToolLocator.HasRar)
			{
				AppendLog($"WinRAR: {ToolLocator.RarPath}", ColorTextSub);
			}
			else
			{
				AppendLog("WinRAR未検出のため、RAR形式は候補から外します（7z / ZIPで動作）。", ColorTextSub);
			}
		}

		/// <summary>ログメッセージの内容から表示色を決める</summary>
		private Color PickLogColor(string message)
		{
			if (message.StartsWith('✖') || message.StartsWith('❌'))
			{
				return ColorError;
			}
			if (message.StartsWith('⚠') || message.Contains("⚠"))
			{
				return ColorWarn;
			}
			if (message.StartsWith('✔') || message.StartsWith('✅'))
			{
				return ColorSuccess;
			}
			if (message.StartsWith("形式決定"))
			{
				return ColorAccent;
			}
			return ColorText;
		}

		private void AppendLog(string message, Color color)
		{
			string line = message.Length == 0 ? "" : $"[{DateTime.Now:HH:mm:ss}] {message}";
			_logBox.SelectionStart = _logBox.TextLength;
			_logBox.SelectionColor = color;
			_logBox.AppendText(line + Environment.NewLine);
			_logBox.SelectionStart = _logBox.TextLength;
			_logBox.ScrollToCaret();
		}

		private void UpdateQueueLabel()
		{
			_lblQueue.Text = _queue.Count > 0 ? $"待機キュー: {_queue.Count} 件" : "";
		}

		/// <summary>ワーカースレッドからのUI更新を安全にマーシャリングする</summary>
		private void SafeInvoke(Action action)
		{
			if (IsDisposed)
			{
				return;
			}
			if (InvokeRequired)
			{
				try
				{
					BeginInvoke(action);
				}
				catch (ObjectDisposedException)
				{
					// フォーム破棄と競合した場合は無視
				}
			}
			else
			{
				action();
			}
		}

		// ---- Win32 ----

		[DllImport("dwmapi.dll", PreserveSig = true)]
		private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

		[DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
		private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);
	}

	/// <summary>
	/// ダークテーマに馴染むフラットなプログレスバー（標準ProgressBarは配色変更不可のため自作）。
	/// </summary>
	public class FlatProgressBar : Control
	{
		private int _value;

		/// <summary>進捗値（0-100）</summary>
		public int Value
		{
			get => _value;
			set
			{
				int clamped = Math.Clamp(value, 0, 100);
				if (clamped != _value)
				{
					_value = clamped;
					Invalidate();
				}
			}
		}

		public FlatProgressBar()
		{
			SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
			Height = 14;
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			using var back = new SolidBrush(Color.FromArgb(50, 50, 52));
			using var fill = new SolidBrush(Color.FromArgb(86, 156, 214));

			g.FillRectangle(back, ClientRectangle);
			int width = (int)(ClientRectangle.Width * (_value / 100.0));
			if (width > 0)
			{
				g.FillRectangle(fill, 0, 0, width, ClientRectangle.Height);
			}
		}
	}

	/// <summary>
	/// タスクバーアイコンへの進捗表示（ITaskbarList3）。最小化中でも圧縮進捗が見える。
	/// </summary>
	public static class TaskbarProgress
	{
		[ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface ITaskbarList3
		{
			// ITaskbarList
			void HrInit();
			void AddTab(IntPtr hwnd);
			void DeleteTab(IntPtr hwnd);
			void ActivateTab(IntPtr hwnd);
			void SetActiveAlt(IntPtr hwnd);
			// ITaskbarList2
			void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
			// ITaskbarList3
			void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
			void SetProgressState(IntPtr hwnd, int tbpFlags);
		}

		[ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
		private class TaskbarInstance { }

		private const int TBPF_NOPROGRESS = 0;
		private const int TBPF_NORMAL = 2;

		private static readonly ITaskbarList3? Instance = CreateInstance();

		private static ITaskbarList3? CreateInstance()
		{
			try
			{
				var instance = (ITaskbarList3)new TaskbarInstance();
				instance.HrInit();
				return instance;
			}
			catch
			{
				return null; // 古いOS等では進捗表示なしで動作
			}
		}

		public static void SetValue(IntPtr hwnd, int value, int max)
		{
			try
			{
				Instance?.SetProgressState(hwnd, TBPF_NORMAL);
				Instance?.SetProgressValue(hwnd, (ulong)value, (ulong)max);
			}
			catch { /* 進捗表示の失敗は無害 */ }
		}

		public static void Clear(IntPtr hwnd)
		{
			try
			{
				Instance?.SetProgressState(hwnd, TBPF_NOPROGRESS);
			}
			catch { /* 同上 */ }
		}
	}

	/// <summary>
	/// SHFileOperationによるゴミ箱への移動（外部参照なしでUndo可能な削除を行う）。
	/// </summary>
	public static class RecycleBin
	{
		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct SHFILEOPSTRUCT
		{
			public IntPtr hwnd;
			public uint wFunc;
			public string pFrom;
			public string? pTo;
			public ushort fFlags;
			public bool fAnyOperationsAborted;
			public IntPtr hNameMappings;
			public string? lpszProgressTitle;
		}

		private const uint FO_DELETE = 0x0003;
		private const ushort FOF_ALLOWUNDO = 0x0040;
		private const ushort FOF_NOCONFIRMATION = 0x0010;

		[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
		private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

		/// <summary>フォルダ／ファイルをゴミ箱へ移動する。成功したらtrue</summary>
		public static bool MoveToRecycleBin(string path)
		{
			return MoveToRecycleBin(new[] { path });
		}

		/// <summary>複数のフォルダ／ファイルをまとめてゴミ箱へ移動する。成功したらtrue</summary>
		public static bool MoveToRecycleBin(IReadOnlyList<string> paths)
		{
			// pFromはヌル区切りで複数指定でき、末尾はダブルヌル終端が必要
			var sb = new System.Text.StringBuilder();
			foreach (string path in paths)
			{
				sb.Append(path.TrimEnd('\\', '/')).Append('\0');
			}
			sb.Append('\0');

			var fileOp = new SHFILEOPSTRUCT
			{
				wFunc = FO_DELETE,
				pFrom = sb.ToString(),
				fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION,
			};
			int result = SHFileOperation(ref fileOp);
			return result == 0 && !fileOp.fAnyOperationsAborted;
		}
	}
}
