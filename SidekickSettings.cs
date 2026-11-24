using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace GeminiSidekick
{
    public sealed class SidekickSettings
    {
        public string CustomAppsFolder { get; set; } = string.Empty;
        public List<string> ExtraFolders { get; set; } = new();
        public int SilenceMsToSend { get; set; } = 1000;

        public List<string> GetAllCustomFolders()
        {
            var result = new List<string>();
            if (!string.IsNullOrWhiteSpace(CustomAppsFolder))
                result.Add(CustomAppsFolder.Trim());
            if (ExtraFolders != null)
            {
                foreach (var f in ExtraFolders)
                {
                    if (!string.IsNullOrWhiteSpace(f))
                        result.Add(f.Trim());
                }
            }
            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    internal static class SidekickSettingsStore
    {
        public static string SettingsFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GeminiSidekick",
            "settings.json");

        public static SidekickSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                    return new SidekickSettings();

                var json = File.ReadAllText(SettingsFilePath);
                return JsonSerializer.Deserialize<SidekickSettings>(json) ?? new SidekickSettings();
            }
            catch
            {
                return new SidekickSettings();
            }
        }

        public static void Save(SidekickSettings settings)
        {
            var dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settings ?? new SidekickSettings(), options);
            File.WriteAllText(SettingsFilePath, json);
        }
    }

    internal sealed class SettingsDialog : Form
    {
        private readonly ListBox _foldersList;
        private readonly NumericUpDown _silenceNumeric;

        public List<string> SelectedFolders => _foldersList.Items.Cast<string>().ToList();
        public int SilenceMs => (int)_silenceNumeric.Value;

        public SettingsDialog(SidekickSettings current)
        {
            Text = "Настройки GeminiSidekick";
            Size = new Size(520, 340);
            MinimumSize = new Size(460, 300);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var lblFolders = new Label
            {
                Text = "Дополнительные папки с ярлыками и программами:",
                Location = new Point(16, 14),
                AutoSize = true
            };

            _foldersList = new ListBox
            {
                Location = new Point(16, 38),
                Size = new Size(360, 175)
            };
            foreach (var folder in (current?.GetAllCustomFolders() ?? new List<string>()))
                _foldersList.Items.Add(folder);

            var btnAdd = new Button
            {
                Text = "Добавить...",
                Location = new Point(388, 38),
                Size = new Size(100, 30)
            };
            btnAdd.Click += (_, _) =>
            {
                using var fbd = new FolderBrowserDialog
                {
                    Description = "Выберите папку с ярлыками (.lnk) или исполняемыми файлами",
                    UseDescriptionForTitle = true
                };
                if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
                {
                    var exists = _foldersList.Items.Cast<string>()
                        .Any(x => string.Equals(x, fbd.SelectedPath, StringComparison.OrdinalIgnoreCase));
                    if (!exists)
                        _foldersList.Items.Add(fbd.SelectedPath);
                }
            };

            var btnRemove = new Button
            {
                Text = "Удалить",
                Location = new Point(388, 76),
                Size = new Size(100, 30)
            };
            btnRemove.Click += (_, _) =>
            {
                if (_foldersList.SelectedIndex >= 0)
                    _foldersList.Items.RemoveAt(_foldersList.SelectedIndex);
            };

            var lblSilence = new Label
            {
                Text = "Пауза тишины перед отправкой (мс):",
                Location = new Point(16, 228),
                AutoSize = true
            };

            _silenceNumeric = new NumericUpDown
            {
                Location = new Point(250, 225),
                Size = new Size(126, 24),
                Minimum = 300,
                Maximum = 10000,
                Increment = 100,
                Value = Math.Clamp(current?.SilenceMsToSend ?? 1000, 300, 10000)
            };

            var btnOk = new Button
            {
                Text = "Сохранить",
                DialogResult = DialogResult.OK,
                Location = new Point(288, 264),
                Size = new Size(96, 30)
            };

            var btnCancel = new Button
            {
                Text = "Отмена",
                DialogResult = DialogResult.Cancel,
                Location = new Point(392, 264),
                Size = new Size(96, 30)
            };

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[]
            {
                lblFolders, _foldersList, btnAdd, btnRemove,
                lblSilence, _silenceNumeric, btnOk, btnCancel
            });
        }
    }
}