using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace SimTelemetry;

/// <summary>Tarjeta "Pantalla en el teléfono" de la pestaña Overlay: activar, dirección y código QR.</summary>
public partial class MainWindow
{
    private string? _shownPhoneUrl;

    private void InitPhone()
    {
        _loading = true;
        PhoneEnabled.IsChecked = _app.Settings.Phone.Enabled;
        _loading = false;
        _app.Phone.Changed += () => Dispatcher.BeginInvoke(RefreshPhone);
        RefreshPhone();
    }

    private void OnPhoneChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = PhoneEnabled.IsChecked == true;
        _app.Settings.Phone.Enabled = on;
        if (on) _app.Phone.Start(_app.Settings.Phone.Port);
        else _app.Phone.Stop();
        SaveSoon();
        RefreshPhone();
    }

    private void RefreshPhone()
    {
        var phone = _app.Phone;
        string? url = phone.Url;
        PhonePanel.Visibility = url != null ? Visibility.Visible : Visibility.Collapsed;
        PhoneError.Visibility = _app.Settings.Phone.Enabled && url == null && phone.Error != null ? Visibility.Visible : Visibility.Collapsed;
        PhoneError.Text = phone.Error ?? "";
        if (url == null) return;
        PhoneUrl.Text = url;
        PhoneStatus.Text = url.Contains("://127.0.0.1")
            ? "No se encontró la red de casa: conecta el PC a la Wi-Fi o por cable al router."
            : phone.Clients switch
            {
                0 => "Esperando al teléfono…",
                1 => "1 pantalla conectada",
                int n => $"{n} pantallas conectadas",
            };
        if (url == _shownPhoneUrl) return;
        _shownPhoneUrl = url;
        using var qr = new QRCodeGenerator().CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        byte[] png = new PngByteQRCode(qr).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        PhoneQr.Source = image;
    }
}
