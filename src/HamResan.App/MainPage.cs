using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HamResan.Core;

namespace HamResan.App;

// Experimental foreground-only manual-code UI; see README before use.
public sealed class MainPage : ContentPage
{
    private const long Limit = 20L * 1024 * 1024 * 1024;
    private readonly Entry _name = new() { Text = DeviceInfo.Current.Name, Placeholder = "نام دستگاه" };
    private readonly Picker _addresses = new() { Title = "نشانی شبکهٔ این دستگاه" };
    private readonly Editor _code = new() { Placeholder = "کد دعوت", HeightRequest = 110, FlowDirection = FlowDirection.LeftToRight, AutoSize = EditorAutoSizeOption.Disabled };
    private readonly Label _status = new() { Text = "برای دریافت، ابتدا کد بسازید. برای ارسال، کد گیرنده را وارد کنید." };
    private readonly Label _location = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _receive = new() { Text = "ساخت کد و انتظار برای دریافت" };
    private readonly Button _send = new() { Text = "انتخاب فایل و ارسال" };
    private readonly Button _cancel = new() { Text = "لغو", IsEnabled = false };
    private CancellationTokenSource? _operation;
    private readonly string _received = Path.Combine(FileSystem.Current.AppDataDirectory, "Received");

    public MainPage()
    {
        Title = "هم‌رسان — آزمایشی";
        FlowDirection = FlowDirection.RightToLeft;
        _location.Text = $"فایل‌ها در فضای خصوصی برنامه ذخیره می‌شوند: {_received}";
        var refresh = new Button { Text = "به‌روزرسانی نشانی شبکه" };
        var copy = new Button { Text = "کپی کد دعوت" };
        var share = new Button { Text = "اشتراک‌گذاری فایل دریافت‌شده" };
        refresh.Clicked += (_, _) => RefreshAddresses();
        copy.Clicked += async (_, _) => { if (!string.IsNullOrWhiteSpace(_code.Text)) await Clipboard.Default.SetTextAsync(_code.Text); };
        _receive.Clicked += async (_, _) => await RunAsync(ReceiveAsync);
        _send.Clicked += async (_, _) => await RunAsync(SendAsync);
        _cancel.Clicked += (_, _) => _operation?.Cancel();
        share.Clicked += async (_, _) => await ShareReceivedAsync();
        Content = new ScrollView { Content = new VerticalStackLayout
        {
            Padding = 24, Spacing = 12, MaximumWidthRequest = 720,
            Children = { new Label { Text = "هم‌رسان", FontSize = 30, FontAttributes = FontAttributes.Bold },
                new Label { Text = "نسخهٔ آزمایشی؛ هر دو دستگاه باید در شبکهٔ محلی یکسان و برنامه باز باشد. کد را محرمانه و فقط از راه مطمئن به فرستنده بدهید." },
                _name, _addresses, refresh, _code, copy, _receive, _send, _cancel, _progress, _status, _location, share,
                new Label { Text = "در شبکهٔ مهمان، جداسازی کاربران، VPN یا فایروال ممکن است اتصال برقرار نشود. دریافت خودکار و کشف دستگاه هنوز وجود ندارند." } }
        } };
        RefreshAddresses();
    }

    private void RefreshAddresses()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct().Select(a => a.ToString()).ToList();
        _addresses.ItemsSource = addresses;
        if (addresses.Count > 0) _addresses.SelectedIndex = 0;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_operation is not null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromHours(2));
        _operation = cts; _receive.IsEnabled = _send.IsEnabled = false; _cancel.IsEnabled = true; _progress.Progress = 0;
        try { await action(cts.Token); }
        catch (OperationCanceledException) { _status.Text = "عملیات لغو شد یا زمان آن تمام شد."; }
        catch (Exception) { _status.Text = "عملیات انجام نشد. کد جدید بسازید و اتصال، مجوزها و فضای ذخیره‌سازی را بررسی کنید."; }
        finally { _operation = null; _receive.IsEnabled = _send.IsEnabled = true; _cancel.IsEnabled = false; }
    }

    private static X509Certificate2 CreateIdentity()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=HamResan", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        if (_addresses.SelectedItem is not string address) { _status.Text = "نشانی شبکه‌ای پیدا نشد. اتصال Wi-Fi یا LAN را بررسی کنید."; return; }
        using var identity = CreateIdentity();
        var listener = new TcpListener(IPAddress.Any, 0); listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var invitation = ConnectionInvitation.Create(IPAddress.Parse(address), port, PinnedTls.Fingerprint(identity));
            var verifier = new InvitationVerifier(invitation, TimeSpan.FromMinutes(10));
            _code.Text = invitation.Encode();
            _status.Text = "کد فقط یک بار و تا ده دقیقه معتبر است. آن را خصوصی به فرستنده بدهید و منتظر بمانید.";
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct); wait.CancelAfter(TimeSpan.FromMinutes(10));
            using TcpClient client = await listener.AcceptTcpClientAsync(wait.Token);
            listener.Stop();
            _code.Text = "";
            await using PinnedConnection connection = await InvitationPairing.AcceptAsync(client, identity, verifier,
                (request, _) => MainThread.InvokeOnMainThreadAsync(() => DisplayAlert("درخواست اتصال", $"دستگاه {request.SenderName} قصد ارسال فایل دارد. فقط اگر انتظار این فرستنده را دارید بپذیرید.\n\nاثر انگشت: {request.SenderFingerprint}", "قبول", "رد")), ct);
            _status.Text = "منتظر مشخصات فایل‌ها…";
            bool result = await TransferEngine.ReceiveAsync(connection, _received, Limit,
                (offer, _) => MainThread.InvokeOnMainThreadAsync(() => DisplayAlert("درخواست دریافت", $"{string.Join("\n", offer.Files.Select(f => f.Name))}\n\nحجم کل: {offer.Files.Sum(f => f.Size):N0} بایت\nمحل: فضای خصوصی برنامه", "دریافت", "رد")), MakeProgress(), ct);
            _status.Text = result ? "فایل‌های تأییدشده دریافت شدند؛ از دکمهٔ اشتراک‌گذاری برای ذخیره یا ارسال آن‌ها استفاده کنید." : "درخواست دریافت رد شد.";
        }
        finally { listener.Stop(); _code.Text = ""; }
    }

    private async Task SendAsync(CancellationToken ct)
    {
        ConnectionInvitation invitation = ConnectionInvitation.Decode((_code.Text ?? "").Trim());
        string name = (_name.Text ?? "").Trim();
        var picked = (await FilePicker.Default.PickMultipleAsync()).ToArray();
        if (picked.Length == 0) return;
        if (picked.Length > Protocol.MaxFiles) { _status.Text = "تعداد فایل‌ها بیش از حد مجاز است."; return; }
        string staging = Path.Combine(FileSystem.Current.CacheDirectory, "send-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var files = new List<OutgoingFile>(); long total = 0;
            foreach (var file in picked)
            {
                ct.ThrowIfCancellationRequested();
                _status.Text = "آماده‌سازی فایل‌ها در حافظهٔ موقت برنامه…";
                string path = Path.Combine(staging, Guid.NewGuid().ToString("N"));
                await using (Stream input = await file.OpenReadAsync())
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, true))
                    await input.CopyToAsync(output, 256 * 1024, ct);
                long size = new FileInfo(path).Length;
                total = checked(total + size);
                if (total > Limit) throw new IOException("File limit exceeded.");
                files.Add(new OutgoingFile(new FileOffer(file.FileName, size, file.ContentType), () => File.OpenRead(path)));
            }
            using var identity = CreateIdentity();
            using var client = new TcpClient(invitation.Address.AddressFamily);
            _status.Text = "در حال اتصال؛ منتظر تأیید گیرنده…";
            await client.ConnectAsync(invitation.Address, invitation.Port, ct);
            await using PinnedConnection connection = await InvitationPairing.ConnectAsync(client, identity, invitation, name, ct);
            bool result = await TransferEngine.SendAsync(connection, files, MakeProgress(), ct);
            _status.Text = result ? "ارسال و بررسی فایل‌ها کامل شد." : "گیرنده درخواست فایل را رد کرد.";
        }
        finally { Directory.Delete(staging, true); }
    }

    private IProgress<TransferProgress> MakeProgress() => new Progress<TransferProgress>(p =>
    {
        _progress.Progress = p.FileSize == 0 ? 1 : (double)p.BytesTransferred / p.FileSize;
        double speed = p.Elapsed.TotalSeconds > 0 ? p.BytesTransferred / p.Elapsed.TotalSeconds : 0;
        string remaining = speed > 0 ? TimeSpan.FromSeconds(Math.Min((p.FileSize - p.BytesTransferred) / speed, 86400)).ToString(@"hh\:mm\:ss") : "--:--:--";
        _status.Text = $"فایل {p.FileIndex + 1}: {p.BytesTransferred:N0} از {p.FileSize:N0} بایت | {speed / 1024 / 1024:F1} MiB/s | باقی‌مانده {remaining}";
    });

    private async Task ShareReceivedAsync()
    {
        if (!Directory.Exists(_received) || Directory.GetFiles(_received).Length == 0) { _status.Text = "فایل دریافت‌شده‌ای وجود ندارد."; return; }
        string[] files = Directory.GetFiles(_received).Where(f => !Path.GetFileName(f).StartsWith(".hamresan-", StringComparison.Ordinal)).ToArray();
        string? selected = await DisplayActionSheet("انتخاب فایل", "لغو", null, files.Select(Path.GetFileName).ToArray()!);
        string? path = files.FirstOrDefault(f => Path.GetFileName(f) == selected);
        if (path is not null) await Share.Default.RequestAsync(new ShareFileRequest { Title = "فایل دریافت‌شده", File = new ShareFile(path) });
    }
}
