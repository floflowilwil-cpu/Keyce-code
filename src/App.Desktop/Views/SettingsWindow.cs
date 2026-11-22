using System;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace App.Desktop.Views;

/// <summary>Paramètres de l'application, enregistrés dans un petit fichier JSON.</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";      // "Dark" ou "Light"
    public double EditorFontSize { get; set; } = 14;

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KeyceCode", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s is not null)
                {
                    s.EditorFontSize = Math.Clamp(s.EditorFontSize, 10, 28);
                    return s;
                }
            }
        }
        catch (Exception) { /* fichier abîmé : on repart des valeurs par défaut */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* impossible d'écrire : les réglages ne seront pas mémorisés */ }
    }
}

/// <summary>Fenêtre Paramètres (construite en C#, aucun XAML).</summary>
public sealed class SettingsWindow : Window
{
    public event EventHandler? SettingsChanged;

    public SettingsWindow(AppSettings settings)
    {
        Title = "Paramètres";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var theme = new ComboBox
        {
            ItemsSource = new[] { "Sombre", "Clair" },
            SelectedIndex = settings.Theme == "Light" ? 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var sizeText = new TextBlock { Text = $"{settings.EditorFontSize:0} px" };
        var slider = new Slider
        {
            Minimum = 10,
            Maximum = 28,
            Value = settings.EditorFontSize,
            TickFrequency = 1,
            IsSnapToTickEnabled = true
        };

        theme.SelectionChanged += (_, _) =>
        {
            settings.Theme = theme.SelectedIndex == 1 ? "Light" : "Dark";
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        };

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            settings.EditorFontSize = Math.Round(slider.Value);
            sizeText.Text = $"{settings.EditorFontSize:0} px";
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        };

        var close = new Button { Content = "Fermer", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Apparence", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Thème" },
                theme,
                new TextBlock { Text = "Éditeur", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) },
                new TextBlock { Text = "Taille de la police" },
                slider,
                sizeText,
                new TextBlock
                {
                    Text = "Les paramètres sont enregistrés automatiquement.",
                    Opacity = 0.6,
                    FontSize = 12,
                    Margin = new Thickness(0, 10, 0, 0)
                },
                close
            }
        };
    }
}

// Partie de MainWindow : ouvre la fenêtre et applique les réglages.
public partial class MainWindow
{
    private readonly AppSettings _settings = AppSettings.Load();

    private void ApplySettings()
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant =
                _settings.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        }
        //EditorBox.FontSize = Math.Clamp(_settings.EditorFontSize, 10, 28);
    }

    private global::App.Core.Settings.JsonSettingsService? _svc;

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        // Le thème peut avoir été changé par le menu : on repart de l'état réel.
        _settings.Theme = ActualThemeVariant == ThemeVariant.Light ? "Light" : "Dark";

        if (_svc is null)
        {
            _svc = new global::App.Core.Settings.JsonSettingsService();
            _svc.Changed += (key, value) =>
            {
                if (key == "workbench.colorTheme")
                    _settings.Theme = value;
                else if (key == "editor.fontSize" &&
                         double.TryParse(value, System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out var size))
                    _settings.EditorFontSize = (int)size;
                else
                    return;

                ApplySettings();
                _settings.Save();
                if (_vm is not null) _vm.StatusMessage = "Paramètres enregistrés";
            };
        }

        // Aligne la page sur les réglages actuels
        _svc.Set("workbench.colorTheme", _settings.Theme);
        _svc.Set("editor.fontSize",
            _settings.EditorFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var win = new Window
        {
            Title = "Paramètres",
            Width = 1000,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new SettingsView
            {
                DataContext = new global::App.Desktop.ViewModels.SettingsViewModel(_svc)
            }
        };
        await win.ShowDialog(this);
    }
}
