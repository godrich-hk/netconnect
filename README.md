# هم‌رسان (HamResan)

**نسخهٔ آزمایشی و ناتمام است؛ برای فایل‌های حساس یا استفادهٔ عمومی آماده نیست.** هدف، دو فایل نصبی Android و Windows است. رابط اولیه و جریان ساخت این بسته‌ها اضافه شده‌اند، اما تا وقتی build و نصب و انتقال واقعی روی دستگاه‌ها تأیید نشوند نباید آن‌ها را محصول آماده دانست. نتیجهٔ دقیق build در `docs/TESTING.md` است.

## گردش آزمایشی فعلی

۱. برنامه را روی هر دو دستگاه و در یک LAN بدون جداسازی کاربران باز نگه دارید. در دستگاه گیرنده نشانی شبکه را انتخاب و «ساخت کد و انتظار» را بزنید. کد طولانی و تصادفی را فقط از مسیر محرمانه و قابل‌اعتماد برای فرستنده بفرستید؛ دارندهٔ کد می‌تواند درخواست اتصال کند.
۲. فرستنده کد را وارد و فایل‌ها را انتخاب می‌کند. گیرنده نام و اثر انگشت فرستنده را می‌بیند و اتصال را قبول یا رد می‌کند. سپس فهرست و حجم فایل‌ها را جداگانه قبول یا رد می‌کند. پیش از این تأییدها محتوای فایل ارسال نمی‌شود.
۳. انتقال داخل TLS و به‌صورت جریانی با بررسی SHA-256 انجام می‌شود. فایل در فضای خصوصی برنامه ذخیره و با دکمهٔ اشتراک‌گذاری در دسترس قرار می‌گیرد. نام تکراری بازنویسی نمی‌شود. هنگام کار برنامه باید باز بماند.

مهم: این نسخه هنوز کشف خودکار، چند گیرنده، هویت پایدار و ذخیرهٔ امن آن، اعتماد ذخیره‌شده، تاریخچه، انتخاب محل عمومی ذخیره، دریافت پس‌زمینه، یا رابط انگلیسی ندارد. کد دعوت شش‌رقمی نیست و برای هر انتقال باید دوباره ساخته شود. فایل‌های انتخاب‌شده پیش از ارسال در cache خصوصی کپی می‌شوند و به فضای خالی نیاز دارند؛ در پایان عادی پاک می‌شوند ولی پس از قطع ناگهانی برنامه ممکن است باقی بمانند. هیچ فایلی خودکار باز نمی‌شود. دربارهٔ محدودیت‌های جدی امنیت و شبکه به [امنیت](docs/SECURITY.md) مراجعه کنید.

## ساخت و آزمون

روی Windows با .NET 10 و workloadهای MAUI Android و Windows، Android SDK/JDK و Windows SDK سازگار:

```powershell
dotnet workload install maui-android maui-windows
dotnet build tests/HamResan.Core.Tests/HamResan.Core.Tests.csproj -c Release
dotnet run --project tests/HamResan.Core.Tests/HamResan.Core.Tests.csproj -c Release --no-build
dotnet publish src/HamResan.App/HamResan.App.csproj -f net10.0-android -c Debug -p:AndroidPackageFormats=apk
dotnet publish src/HamResan.App/HamResan.App.csproj -f net10.0-windows10.0.19041.0 -p:TargetFrameworks=net10.0-windows10.0.19041.0 -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true
```

Workflow آزمایشی GitHub Actions از Inno Setup برای ساخت نصب‌کنندهٔ Windows استفاده می‌کند. APK با کلید debug امضا می‌شود و نصب‌کنندهٔ Windows امضای تجاری ندارد؛ این‌ها بسته‌های انتشار عمومی یا قابل‌اعتماد نیستند. فقط پس از موفق شدن workflow می‌توان artifactهای آن را دریافت کرد. فایل‌های باینری فعلاً جزو مخزن نیستند.

[معماری](docs/ARCHITECTURE.md) · [پروتکل](docs/PROTOCOL.md) · [امنیت](docs/SECURITY.md) · [وضعیت آزمون](docs/TESTING.md)
