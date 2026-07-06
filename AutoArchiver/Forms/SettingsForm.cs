using System.Runtime.InteropServices;
using AutoArchiver.Utils;

namespace AutoArchiver.Forms
{
	/// <summary>
	/// 設定ダイアログ。元アイテムの扱い・完了通知・除外パターン・リカバリレコード・パスワードを編集する。
	/// ここで保存した値は「送る」メニュー経由の起動にもそのまま適用される。
	/// </summary>
	public class SettingsForm : Form
	{
		private static readonly Color ColorBack = Color.FromArgb(30, 30, 30);
		private static readonly Color ColorPanel = Color.FromArgb(37, 37, 38);
		private static readonly Color ColorText = Color.FromArgb(230, 230, 230);
		private static readonly Color ColorTextSub = Color.FromArgb(150, 150, 150);
		private static readonly Color ColorBorder = Color.FromArgb(70, 70, 72);
		private static readonly Color ColorInput = Color.FromArgb(45, 45, 48);

		private readonly AppSettings _settings;

		private readonly RadioButton _rbAsk = new();
		private readonly RadioButton _rbKeep = new();
		private readonly RadioButton _rbRecycle = new();
		private readonly CheckBox _chkNotify = new();
		private readonly CheckBox _chkAlwaysCompress = new();
		private readonly RadioButton _rbAcSevenZip = new();
		private readonly RadioButton _rbAcRar = new();
		private readonly TextBox _txtExclude = new();
		private readonly NumericUpDown _numRecovery = new();
		private readonly TextBox _txtPassword = new();
		private readonly CheckBox _chkShowPassword = new();

		public SettingsForm(AppSettings settings)
		{
			_settings = settings;
			InitializeLayout();
			LoadValues();
		}

		private void InitializeLayout()
		{
			Text = "AutoArchiver - 設定";
			Size = new Size(480, 690);
			FormBorderStyle = FormBorderStyle.FixedDialog;
			MaximizeBox = false;
			MinimizeBox = false;
			StartPosition = FormStartPosition.CenterParent;
			BackColor = ColorBack;
			ForeColor = ColorText;
			Font = new Font("Yu Gothic UI", 9.5f);

			HandleCreated += (_, _) =>
			{
				int enabled = 1;
				DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
			};

			var layout = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16), AutoScroll = true };

			int y = 8;

			// --- 元アイテムの扱い ---
			layout.Controls.Add(MakeSectionLabel("圧縮成功後の元ファイル・フォルダ", ref y));
			ConfigureRadio(_rbAsk, "毎回ダイアログで確認する", ref y, layout);
			ConfigureRadio(_rbKeep, "常に残す（確認しない）", ref y, layout);
			ConfigureRadio(_rbRecycle, "常にゴミ箱へ移動（確認しない）", ref y, layout);
			y += 10;

			// --- 通知 ---
			layout.Controls.Add(MakeSectionLabel("通知", ref y));
			_chkNotify.Text = "全キュー完了時に通知する（音 + バルーン）";
			_chkNotify.Location = new Point(12, y);
			_chkNotify.Width = 400;
			_chkNotify.ForeColor = ColorText;
			layout.Controls.Add(_chkNotify);
			y += 34;

			// --- 圧縮判定 ---
			layout.Controls.Add(MakeSectionLabel("圧縮判定", ref y));
			_chkAlwaysCompress.Text = "既圧縮データ主体（動画・画像だけ等）でも圧縮形式を使う";
			_chkAlwaysCompress.Location = new Point(12, y);
			_chkAlwaysCompress.Width = 420;
			_chkAlwaysCompress.ForeColor = ColorText;
			layout.Controls.Add(_chkAlwaysCompress);
			y += 28;
			var lblAcNote = new Label
			{
				Text = "類似ファイルの共通部分で1〜2%縮むことがあるが、時間は大幅に増える。\r\n元より大きくなった場合は自動で無圧縮ZIPに作り直される",
				Location = new Point(30, y),
				Size = new Size(410, 34),
				ForeColor = ColorTextSub,
			};
			layout.Controls.Add(lblAcNote);
			y += 40;

			// このケースはベンチで7z/RARの優劣が測れない（両方ほぼ100%と出る）ため形式は設定で選ぶ。
			// ラジオは同一コンテナで1グループになるため、「元アイテムの扱い」のラジオと混ざらないよう専用パネルに隔離する
			var pnlAcFormat = new Panel { Location = new Point(30, y), Size = new Size(410, 26) };
			var lblAcFormat = new Label
			{
				Text = "使う形式:",
				Location = new Point(0, 4),
				Width = 70,
				ForeColor = ColorText,
			};
			pnlAcFormat.Controls.Add(lblAcFormat);
			_rbAcSevenZip.Text = "7z (LZMA2)";
			_rbAcSevenZip.Location = new Point(74, 2);
			_rbAcSevenZip.Width = 110;
			_rbAcSevenZip.ForeColor = ColorText;
			pnlAcFormat.Controls.Add(_rbAcSevenZip);
			_rbAcRar.Text = "RAR5";
			_rbAcRar.Location = new Point(190, 2);
			_rbAcRar.Width = 80;
			_rbAcRar.ForeColor = ColorText;
			pnlAcFormat.Controls.Add(_rbAcRar);
			layout.Controls.Add(pnlAcFormat);
			y += 34;

			void UpdateAcFormatEnabled() => pnlAcFormat.Enabled = _chkAlwaysCompress.Checked;
			_chkAlwaysCompress.CheckedChanged += (_, _) => UpdateAcFormatEnabled();
			UpdateAcFormatEnabled();

			// --- 除外パターン ---
			layout.Controls.Add(MakeSectionLabel("書庫から除外するファイル（1行1パターン、ワイルドカード可）", ref y));
			_txtExclude.Multiline = true;
			_txtExclude.ScrollBars = ScrollBars.Vertical;
			_txtExclude.Location = new Point(12, y);
			_txtExclude.Size = new Size(420, 84);
			StyleInput(_txtExclude);
			layout.Controls.Add(_txtExclude);
			y += 94;

			// --- リカバリレコード ---
			layout.Controls.Add(MakeSectionLabel("RARリカバリレコード（%）　0 = 付けない", ref y));
			_numRecovery.Location = new Point(12, y);
			_numRecovery.Width = 80;
			_numRecovery.Minimum = 0;
			_numRecovery.Maximum = 10;
			_numRecovery.BackColor = ColorInput;
			_numRecovery.ForeColor = ColorText;
			_numRecovery.BorderStyle = BorderStyle.FixedSingle;
			layout.Controls.Add(_numRecovery);
			var lblRrNote = new Label
			{
				Text = "RAR形式が選ばれたときだけ有効。書庫の破損耐性が上がる",
				Location = new Point(104, y + 4),
				Width = 330,
				ForeColor = ColorTextSub,
			};
			layout.Controls.Add(lblRrNote);
			y += 40;

			// --- パスワード ---
			layout.Controls.Add(MakeSectionLabel("書庫パスワード（空 = 暗号化なし）", ref y));
			_txtPassword.Location = new Point(12, y);
			_txtPassword.Width = 300;
			_txtPassword.UseSystemPasswordChar = true;
			StyleInput(_txtPassword);
			layout.Controls.Add(_txtPassword);

			_chkShowPassword.Text = "表示";
			_chkShowPassword.Location = new Point(324, y + 2);
			_chkShowPassword.Width = 70;
			_chkShowPassword.ForeColor = ColorText;
			_chkShowPassword.CheckedChanged += (_, _) => _txtPassword.UseSystemPasswordChar = !_chkShowPassword.Checked;
			layout.Controls.Add(_chkShowPassword);
			y += 30;

			var lblPwNote = new Label
			{
				Text = "7z/RARはファイル名も暗号化。パスワードはこのPCのユーザー資格で暗号化保存",
				Location = new Point(12, y),
				Width = 430,
				ForeColor = ColorTextSub,
			};
			layout.Controls.Add(lblPwNote);
			y += 30;

			// --- OK / キャンセル ---
			var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = ColorPanel, Padding = new Padding(12, 10, 12, 10) };
			var btnCancel = MakeButton("キャンセル");
			btnCancel.Dock = DockStyle.Right;
			btnCancel.DialogResult = DialogResult.Cancel;

			var btnOk = MakeButton("保存");
			btnOk.Dock = DockStyle.Right;
			btnOk.Click += OnOkClick;

			bottomPanel.Controls.Add(btnOk);
			bottomPanel.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
			bottomPanel.Controls.Add(btnCancel);

			Controls.Add(layout);
			Controls.Add(bottomPanel);
			CancelButton = btnCancel;
			AcceptButton = btnOk;
		}

		private Label MakeSectionLabel(string text, ref int y)
		{
			var label = new Label
			{
				Text = text,
				Location = new Point(4, y),
				Width = 430,
				ForeColor = ColorTextSub,
				Font = new Font("Yu Gothic UI", 9f, FontStyle.Bold),
			};
			y += 26;
			return label;
		}

		private void ConfigureRadio(RadioButton rb, string text, ref int y, Panel parent)
		{
			rb.Text = text;
			rb.Location = new Point(12, y);
			rb.Width = 400;
			rb.ForeColor = ColorText;
			parent.Controls.Add(rb);
			y += 28;
		}

		private void StyleInput(TextBox box)
		{
			box.BackColor = ColorInput;
			box.ForeColor = ColorText;
			box.BorderStyle = BorderStyle.FixedSingle;
		}

		private Button MakeButton(string text)
		{
			var button = new Button
			{
				Text = text,
				Width = 110,
				FlatStyle = FlatStyle.Flat,
				BackColor = ColorInput,
				ForeColor = ColorText,
			};
			button.FlatAppearance.BorderColor = ColorBorder;
			return button;
		}

		private void LoadValues()
		{
			_rbAsk.Checked = _settings.DeleteMode == SourceDeleteMode.AskEveryTime;
			_rbKeep.Checked = _settings.DeleteMode == SourceDeleteMode.AlwaysKeep;
			_rbRecycle.Checked = _settings.DeleteMode == SourceDeleteMode.AlwaysRecycle;
			_chkNotify.Checked = _settings.NotifyOnComplete;
			_chkAlwaysCompress.Checked = _settings.AlwaysTryCompress;
			_rbAcRar.Checked = _settings.AlwaysTryCompressFormat == Core.ArchiveFormat.Rar;
			_rbAcSevenZip.Checked = !_rbAcRar.Checked;
			_txtExclude.Text = string.Join(Environment.NewLine, _settings.ExcludePatterns);
			_numRecovery.Value = Math.Clamp(_settings.RecoveryRecordPercent, 0, 10);
			_txtPassword.Text = _settings.Password;
		}

		private void OnOkClick(object? sender, EventArgs e)
		{
			// 引数エスケープを壊す文字はパスワードに使えない
			if (_txtPassword.Text.Contains('"'))
			{
				MessageBox.Show(this, "パスワードにダブルクォート（\"）は使用できません。", "AutoArchiver",
					MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			_settings.DeleteMode = _rbKeep.Checked ? SourceDeleteMode.AlwaysKeep
				: _rbRecycle.Checked ? SourceDeleteMode.AlwaysRecycle
				: SourceDeleteMode.AskEveryTime;
			_settings.NotifyOnComplete = _chkNotify.Checked;
			_settings.AlwaysTryCompress = _chkAlwaysCompress.Checked;
			_settings.AlwaysTryCompressFormat = _rbAcRar.Checked ? Core.ArchiveFormat.Rar : Core.ArchiveFormat.SevenZip;

			var patterns = new List<string>();
			foreach (string line in _txtExclude.Lines)
			{
				if (!string.IsNullOrWhiteSpace(line))
				{
					patterns.Add(line.Trim());
				}
			}
			_settings.ExcludePatterns = patterns.ToArray();
			_settings.RecoveryRecordPercent = (int)_numRecovery.Value;
			_settings.Password = _txtPassword.Text;

			_settings.Save();
			DialogResult = DialogResult.OK;
			Close();
		}

		[DllImport("dwmapi.dll", PreserveSig = true)]
		private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
	}
}
