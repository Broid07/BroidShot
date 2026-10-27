**BroidShot modül geliştirme rehberi**

Bu rehber, araç kutusunun her aracını ayrı bir sınıf kitaplığı olarak geliştirmek için yazıldı. Çatı (`BroidShot.Core` + uygulama kabuğu) hazırdır; pano, medya, dönüştürme, kayıt ve küçük araçlar bu sözleşmeyle birbirinden bağımsız ve paralel geliştirilir. Plan için `docs/ARAC_KUTUSU_PLANI.md` dosyasına bak.

**1. Temel kural**

Bir modül yalnız kendi proje klasörüne (`src/BroidShot.Modules.<Ad>/`) ve kendi doğrulama klasörüne (`tests/<Ad>.Verification/`) dokunur. `BroidShot.Core`, uygulama projesi (`src/BroidShot/`), `build.ps1`, `BroidShot.slnx` ve diğer modüller değiştirilmez. Modülün uygulamaya kaydı (`src/BroidShot/ModuleRegistry.cs` ve `BroidShot.csproj` içindeki proje başvurusu) birleştirme aşamasında yapılır. Böylece paralel çalışan geliştiriciler aynı dosyada çakışmaz.

Core'da eksik bir şey görürsen (yeni bir ortak mesaj türü, yeni bir host servisi) bunu kendi modülünde geçici olarak çöz ve teslim notuna yaz; Core değişikliği birleştirme sırasında tek elden yapılır.

**2. Hedef çatı (TFM)**

Bütün projeler `net10.0-windows10.0.19041.0` hedefler: uygulama, Core, modüller ve doğrulama programları. Bu hedef Windows 10 SDK izdüşümünü (CsWinRT) getirir; böylece `Windows.Data.Pdf`, `Windows.Graphics.Capture`, `Windows.Media` gibi WinRT API'leri ek paket olmadan kullanılabilir. Bedeli, taşınabilir pakete `Microsoft.Windows.SDK.NET.dll` ve `WinRT.Runtime.dll` eklenmesidir. Farklı bir TFM seçme; uygulama, daha yüksek platform sürümü isteyen bir projeye başvuramaz. Daha yeni bir Windows API'si gerekirse çalışma anında `OperatingSystem.IsWindowsVersionAtLeast(...)` ile denetle.

Bütün projelerde `Nullable` açık, `ImplicitUsings` kapalıdır: `using` satırları açıkça yazılır, dosya kapsamlı `namespace` kullanılır. Kullanıcının gördüğü bütün metinler Türkçedir.

**3. Yeni modül projesi**

1. `src/BroidShot.Modules.Sample/` klasörünü `src/BroidShot.Modules.<Ad>/` olarak kopyala.
2. `.csproj` dosyasını `BroidShot.Modules.<Ad>.csproj` olarak adlandır; `RootNamespace` ve `AssemblyName` değerlerini `BroidShot.Modules.<Ad>` yap. TFM'ye, `UseWPF` ayarına ve Core başvurusuna dokunma.
3. Sınıf adlarını, ad alanını ve XAML içindeki `x:Class` değerini değiştir.
4. Modül kimliğini (`Id`) seç: küçük harf, rakam, `.`, `_` veya `-`; en fazla 64 karakter (ör. `clipboard`, `media`, `convert`, `recorder`, `utilities`). Kimlik klasör adı olur ve sonradan değiştirilmemelidir.

NuGet paketi gerekiyorsa yalnız kendi `.csproj` dosyana ekle (ör. PdfSharp, ZXing.Net). Lisansı uygun, bakımı süren ve küçük paketleri tercih et; lisans notunu teslimde belirt.

`build.ps1`, `src/BroidShot.Modules.*` klasörlerindeki projeleri kendiliğinden bulup derler. Modül uygulamaya kaydedilmeden önce de derlenir ve test edilir.

**4. Modül sözleşmesi**

Her modül `BroidShot.Core.IToolModule` arayüzünü uygular:

```csharp
public interface IToolModule
{
    string Id { get; }                        // "clipboard"
    string Title { get; }                     // "Pano"
    string Description { get; }               // sayfa alt başlığı ve palet açıklaması
    string Category { get; }                  // ToolCategories.Clipboard gibi
    string Icon { get; }                      // Segoe Fluent Icons karakteri, ör. ""
    IReadOnlyList<string> Keywords { get; }   // palet araması için eş anlamlılar
    IReadOnlyList<ToolCommand> Commands { get; }
    IReadOnlyList<ToolHotkey> Hotkeys => Array.Empty<ToolHotkey>();
    void Initialize(IToolHost host);
    FrameworkElement CreateView();
    void Shutdown();
}
```

Yaşam döngüsü şöyledir; bütün çağrılar arayüz iş parçacığında yapılır:

- Kabuk açılırken tanım özelliklerini okur, ardından `Initialize(host)` bir kez çağrılır. Ana pencere bu sırada gizli olabilir (tepside başlatma). Pano dinleyicisi gibi arka plan işleri burada başlar. `Initialize` uzun sürmemeli; disk ve ağ işlerini `Task.Run` ile arka plana al.
- `Hotkeys` listesi `Initialize` sonrasında bir kez okunur ve kabuk tarafından kaydedilir. Kaydedilemeyen kısayol Ayarlar → Araçlar bölümünde gösterilir; uygulama durmaz.
- `CreateView()` modül ilk kez açıldığında bir kez çağrılır, sonuç saklanır; sayfalar arasında geçişte durum korunur. Her çağrıda yeni ve başka bir öğeye bağlı olmayan bir öğe döndür.
- `Commands` komut paleti her açıldığında yeniden okunur; liste çalışma anında değişebilir (ör. kayıtlı snippet'ler komut olarak görünebilir).
- `Shutdown()` uygulama kapanırken bir kez çağrılır. Hızlı olmalı ve hata fırlatmamalı. Dinleyicileri durdur, açık dosyaları kapat.

`Initialize` veya `CreateView` hata fırlatırsa hata `errors.log` dosyasına yazılır, kenar çubuğundaki öğe soluklaşır ve modül sayfasında bir hata sayfası görünür. Başarısız modülün komutları palette gösterilmez; diğer araçlar çalışmaya devam eder.

Kategoriler `ToolCategories` sabitleridir: `Yakalama`, `Pano`, `Medya`, `Dosyalar`, `Küçük araçlar`, `Geliştirme`. Kenar çubuğunda bir kategori, o kategorideki ilk modülün kayıt sırasındaki yerinde görünür.

**Komutlar.** `ToolCommand` komut paletindeki bir satırdır:

```csharp
new ToolCommand("PDF'i PNG'ye dönüştür", async token => await ConvertAsync(token))
{
    Id = "convert.pdf-to-png",
    Description = "Sayfaları ayrı PNG dosyalarına çıkarır",
    Keywords = new[] { "pdf", "png", "görsel", "export" },
    NavigateToModuleId = "convert"   // isteğe bağlı: önce ana pencerede bu sayfayı aç
};
new ToolCommand("Panoyu temizle", () => ClearHistory());       // eşzamanlı
ToolCommand.Open("Pano geçmişini aç", "clipboard");             // yalnız sayfa açar
```

Palet kapanınca komut arayüz iş parçacığında çalışır. Asenkron komutun aldığı `CancellationToken`, uygulama kapanınca iptal edilir. Fırlatılan hata günlüğe yazılır ve kırmızı bir bildirimle gösterilir. Uzun işleri komut içinde bekletme; iş takibine (`host.Jobs`) devret.

Arama Türkçe karakterlere duyarsızdır: "donustur" yazmak "Dönüştür" başlığını, "pdf png" yazmak her iki kelimeyi içeren başlığı bulur. Aynı mantığı kendi listelerinde kullanmak için `SearchMatcher.Score(...)` ve `SearchMatcher.Normalize(...)` hazırdır.

**Genel kısayollar.** Modül sabit bir kısayol bildirebilir:

```csharp
public IReadOnlyList<ToolHotkey> Hotkeys => new[]
{
    new ToolHotkey("snippets.picker", "Metin şablonu seç", HotkeyGesture.Parse("Ctrl+Alt+V"), ShowPicker)
};
```

Kullanıcı ayarına bağlı bir kısayol gerekiyorsa `Initialize` içinde `host.RegisterHotkey(gesture, callback)` çağır. Dönen `HotkeyRegistration` nesnesinin `IsRegistered` ve Türkçe `Error` alanları vardır; ayar değişince eskisini `Dispose()` edip yenisini kaydet. Uygulama kapanınca bütün kayıtlar kabuk tarafından bırakılır. Kısayol metni `"Ctrl+Alt+K"` biçiminde saklanır; `HotkeyGesture.TryParse`, `ToString()` ve ekranda göstermek için `DisplayText` ("Ctrl + Alt + K") kullanılır.

**5. Host servisleri**

`Initialize` içinde gelen `IToolHost`, yalnız o modüle ayrılmış bir nesnedir. Sakla ve modül boyunca kullan.

- `host.Settings`: `<DataRoot>/modules/<id>/settings.json` için tipli JSON deposu. `host.Settings.Load<MySettings>()` dosya yoksa veya bozuksa varsayılanları döndürür; `host.Settings.Save(settings)` atomik yazar. Ayar sınıfına yeni özellik eklerken varsayılan değer ver; eski dosyalar okunmaya devam eder.
- `host.DataFolder`: `<DataRoot>/modules/<id>/data`. İlk erişimde oluşturulur. Veritabanı, önbellek ve küçük resimler buraya.
- `host.DataRoot`: genel veri kökü. `BROIDSHOT_DATA_DIR` ortam değişkeni varsa onu kullanır (testler bunu yalıtım için ayarlar). Aynı bilgiye `DataPaths.Root` ile de ulaşılır.
- `host.ShowToast(...)`: sağ altta küçük bildirim. Ana pencere gizliyken de görünür, odağı çalmaz. Üzerine gelince süre durur.

  ```csharp
  host.ShowToast("Video bağlantısı algılandı", url, "İndir", () => StartDownload(url));
  host.ShowToast(new ToastOptions("İndirme bitti", fileName) { Kind = ToastKind.Success, Duration = TimeSpan.FromSeconds(8) });
  ```

- `host.Navigate(moduleId)` ve `host.NavigateToSelf()`: ana pencereyi gösterir ve ilgili sayfayı açar. `"settings"` Ayarlar sayfasıdır.
- `host.Messages`: modüller arası tipli yayınla/abone ol. İşleyiciler arayüz iş parçacığında çalışır; bir işleyicinin hatası diğerlerini durdurmaz. Aboneliği `Shutdown` içinde `Dispose()` et.

  ```csharp
  _subscription = host.Messages.Subscribe<UrlCopiedMessage>(OnUrlCopied);
  host.Messages.Publish(new UrlCopiedMessage(url, looksLikeVideo, sourceApp, DateTimeOffset.Now));
  ```

  Ortak mesaj türleri `src/BroidShot.Core/Messaging/CommonMessages.cs` içindedir: `UrlCopiedMessage` (Pano → Medya; hassas içerik asla yayınlanmaz), `MediaDownloadRequestedMessage` (Medya sayfasını bağlantıyla açma isteği) ve `FilesToConvertMessage` (Dönüştürme sayfasına dosya gönderme). Önerilen akış: Pano bağlantı kopyalandığını yayınlar; Medya desteklediği bir video bağlantısıysa "İndir" düğmeli bildirim gösterir; kullanıcı onaylamadan indirme başlamaz. Yalnız kendi içinde kullandığın mesaj türlerini kendi projende tanımlayabilirsin; başka bir modülün anlaması gereken tür Core'a birleştirme sırasında eklenir.

- `host.Jobs`: kenar çubuğunun altındaki iş listesi (başlık, ilerleme çubuğu, durum satırı, iptal düğmesi). En kolay yol `RunAsync`:

  ```csharp
  await host.Jobs.RunAsync("Video indiriliyor", async (job, token) =>
  {
      job.Report(null, "Bilgi alınıyor…");          // null = belirsiz ilerleme
      ...
      job.Report(received / (double)total, $"%{received * 100 / total}");
  });
  ```

  İptal edilen iş (`OperationCanceledException`) "İptal edildi", hata veren iş hata mesajıyla işaretlenir; hata günlüğe yazılır ve yeniden fırlatılmaz. `RunAsync` son durumu (`ToolJobState`) döndürür. Elle yönetmek istersen `host.Jobs.Start(title)` ile başlat; `Report`, `Complete`, `Fail`, `Cancelled` metotlarını kullan. `ToolJob` iş parçacığı güvenlidir; arka plandan çağrılabilir.
- `host.ExternalTools`: yt-dlp, ffmpeg gibi harici araçlar (aşağıda).
- `host.RegisterHotkey(...)`: yukarıda anlatıldı; yalnız arayüz iş parçacığında çağrılır.
- `host.Log(message, exception)`: `<DataRoot>/errors.log` dosyasına modül kimliğiyle yazar. Kullanıcı verisini (pano içeriği, dosya içerikleri) günlüğe yazma.
- `host.Dispatcher`: arayüz iş parçacığı. Arka plandan arayüze dönmek için `host.Dispatcher.InvokeAsync(...)`. `ShowToast`, `Navigate`, `Messages.Publish` ve `ToolJob` metotları zaten kendisi aktarır.

**6. Harici araçlar (yt-dlp, ffmpeg)**

`IExternalToolManager` bir aracı `<DataRoot>/tools/<ad>/current/` altına kurar, sürüm bilgisini `<DataRoot>/tools/<ad>/tool.json` dosyasında tutar. Kurallar sabittir:

- Bütün bağlantılar HTTPS olmalıdır; HTTP'ye yönlenen bir yanıt da reddedilir.
- Her indirme SHA-256 ile doğrulanır. Özet ya sürüm tanımında verilir (`Sha256`) ya da sürümün özet dosyasından (`ChecksumFileUrl`, `sha256sum` veya BSD biçimi) okunur. Özet yoksa, dosya özet listesinde yoksa ya da değer tutmazsa hiçbir şey kurulmaz ve geçici dosyalar silinir.
- Zip arşivleri güvenli açılır: klasör dışına çıkan yollar reddedilir, tek kök klasör varsa kaldırılır.
- Kurulum önce geçici klasöre yapılır, sonra tek adımda yerine konur. Araç çalışırken güncellenemiyorsa eski sürüm korunur.

Hazır tanımlar `KnownExternalTools` içindedir:

- `KnownExternalTools.YtDlp`: github.com/yt-dlp/yt-dlp son sürümündeki `yt-dlp.exe`, `SHA2-256SUMS` ile doğrulanır. Sürüm ve dosya bağlantıları GitHub API'den okunur; API kullanılamazsa `releases/latest/download/` bağlantılarına düşer.
- `KnownExternalTools.FFmpeg`: github.com/BtbN/FFmpeg-Builds `latest` sürümündeki en yeni kararlı dalın win64 GPL zip'i (ör. `ffmpeg-n9.0-latest-win64-gpl-9.0.zip`), `checksums.sha256` ile doğrulanır. Dosya adı çalışma anında API'den seçilir; sürüm numarası koda gömülü değildir. `KnownExternalTools.CreateFFmpeg(lgpl: true)` LGPL derlemesini `ffmpeg-lgpl` adıyla kurar. `ffprobe.exe`, `ffmpeg.exe` ile aynı `bin` klasöründedir: `Path.Combine(host.ExternalTools.GetInstallDirectory(KnownExternalTools.FFmpeg), "bin", "ffprobe.exe")`.

Kullanım, iş takibiyle birlikte:

```csharp
string ytDlp = "";
await host.Jobs.RunAsync("yt-dlp hazırlanıyor", async (job, token) =>
{
    var progress = new Progress<ExternalToolProgress>(p => job.Report(p.Fraction, p.Describe()));
    ytDlp = await host.ExternalTools.EnsureUpToDateAsync(KnownExternalTools.YtDlp, progress, token);
});
```

- `EnsureAsync`: kuruluysa yolu hemen döndürür (ağ yok); değilse son doğrulanmış sürümü indirir.
- `EnsureUpToDateAsync`: aynısı, ek olarak tanımdaki `UpdateInterval` süresi (yt-dlp 3 gün, ffmpeg 14 gün) geçtiyse güncelleme denetler ve kurar. Güncelleme başarısız olursa günlüğe yazar ve kurulu sürümü döndürür.
- `CheckForUpdateAsync`: son sürümün SHA-256 değerini kurulu olanla karşılaştırır; "Güncellemeleri denetle" düğmesi için.
- `GetStatus`, `TryGetExecutable`: ağ kullanmadan durum ve yol.

`Progress<T>` nesnesini arayüz iş parçacığında oluşturursan raporlar arayüz iş parçacığına gelir. Hatalar `ExternalToolException` türündedir ve mesajı kullanıcıya gösterilebilir Türkçe metindir. Başka bir araç için kendi `ExternalToolDefinition` nesneni oluşturabilirsin; çözümleyici yalnız HTTPS kaynak ve özet bilgisi döndürmelidir. Aracı çalıştırırken (`Process`) argümanları `ProcessStartInfo.ArgumentList` ile ver, `UseShellExecute = false` kullan ve kullanıcı girdisini komut satırına birleştirme.

**7. Tema ve görünüm**

Ortak tema `src/BroidShot.Core/Themes/Theme.xaml` dosyasındadır ve uygulama kaynaklarına eklenir. Modül sayfaları ve modülün açtığı pencereler `Button`, `TextBox`, `CheckBox`, `ComboBox`, `ScrollBar`, `ProgressBar` için siyah zemin ve ince kırmızı vurgulu stilleri kendiliğinden alır. `Application.Current.Resources` içeriğini değiştirme.

Adlandırılmış anahtarlar `{StaticResource ...}` ile kullanılır:

- Fırçalar: `CanvasBrush`, `PanelBrush`, `RaisedBrush`, `StrokeBrush`, `TextBrush`, `MutedBrush`, `SecondaryTextBrush`, `AccentBrush` (#D94046), `AccentSoftBrush`, `DividerBrush`, `SelectedBrush`, `SidebarBrush`.
- Yazı tipleri: `AppFont` (Segoe UI Variable Text), `HeadingFont` (Segoe UI Variable Display), `IconFont` (Segoe Fluent Icons, yedek Segoe MDL2 Assets).
- Metin stilleri: `PageHeading`, `SectionHeading`, `MutedText`, `SmallLabel`, `IconText`.
- Kutular ve düğmeler: `Card` (Border), `AccentButton`, `SubtleButton`, `IconButton`, `NavButton` (`Tag="selected"` seçili görünüm).

Sayfa başlığı ve alt başlığı kabuk tarafından `Title` ve `Description` değerlerinden çizilir; sayfanın kendisi başlık tekrarlamamalı. Sayfa kök öğesine dış boşluk verme; içerik genişliğini `MaxWidth="620"` gibi sınırlarla düzenle. Örnek için `SampleView.xaml` dosyasına bak.

Tema kabuk dışında (tasarımcı, test penceresi) gerekirse açıkça eklenebilir:

```xml
<ResourceDictionary Source="pack://application:,,,/BroidShot.Core;component/Themes/Theme.xaml"/>
```

C# tarafında `ThemeResources.Load()` aynı sözlüğü verir. Simgeler Segoe Fluent Icons karakter kodlarıdır (ör. `""` pano, `""` indir, `""` belge). Windows 11'de yerleşiktir.

**8. Doğrulama programı**

Her modül `tests/<Ad>.Verification/` altında küçük bir konsol programıyla doğrulanır; mevcut `tests/*.Verification` ve `tests/Core.Verification` örneklerini izle. `build.ps1 -Verify`, `tests` altındaki her `*.Verification.csproj` projesini kendiliğinden bulur ve `-c Verification` yapılandırmasıyla çalıştırır. Çıkış kodu 0 değilse derleme başarısız sayılır.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/BroidShot.Modules.<Ad>/BroidShot.Modules.<Ad>.csproj" />
  </ItemGroup>
</Project>
```

Programın yapısı:

- `Main` metodu `[STAThread]` olsun (WPF nesneleri için); her testi ad ve gövde olarak listele, `PASS ...` / `FAIL ...` yaz, sonunda özet satırı ver ve hata varsa 1 döndür.
- İlk iş olarak `BROIDSHOT_DATA_DIR` değişkenini çıktı klasöründe `test-data/<guid>` gibi yalıtılmış bir klasöre ayarla. Gerçek kullanıcı verisine, Windows başlangıç kaydına ve gerçek panoya dokunma; panoyu değiştiren testler `--clipboard-verify` gibi açık bir bayrağa bağlanmalı.
- Gerçek ağ kullanma. HTTP gerektiren kod için `ExternalToolManager(toolsRoot, handler)` yapıcısına sahte bir `HttpMessageHandler` ver (bkz. `tests/Core.Verification/Program.cs` içindeki `FakeHandler`).
- Modülü kabuk olmadan sınamak için `IToolHost` arayüzünün küçük bir test uygulamasını yaz: `ModuleSettings`, `MessageBus(dispatcher: null)` ve `JobTracker { FinishedJobLinger = TimeSpan.Zero }` doğrudan kullanılabilir. Dispatcher yerine `Dispatcher.CurrentDispatcher` verilebilir.
- Kabul ölçütlerini birebir sına (ör. Medya: yt-dlp yokken akış, bozuk özet reddi; Pano: hassas içeriğin maskelenmesi ve bir saat sonra diskten silinmesi; Dönüştürme: çok sayfalı PDF'in sayfa sayısı ve boyutları).

Geliştirme sırasında yalnız kendi projeni derleyip testini çalıştırmak yeterlidir; böylece paralel çalışan ajanlar aynı `obj` klasörlerinde çakışmaz:

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.tools\cli-home"; $env:NUGET_PACKAGES = "$PWD\.tools\nuget"; $env:DOTNET_ROOT = "$PWD\.tools\dotnet"
.\.tools\dotnet\dotnet.exe restore tests\<Ad>.Verification\<Ad>.Verification.csproj --configfile NuGet.Config
.\.tools\dotnet\dotnet.exe run --project tests\<Ad>.Verification\<Ad>.Verification.csproj --no-restore -c Verification
```

**9. Birleştirme adımları**

Modül tesliminden sonra birleştirmeyi yapan kişi şunları yapar:

1. `src/BroidShot/BroidShot.csproj` içine `<ProjectReference Include="../BroidShot.Modules.<Ad>/BroidShot.Modules.<Ad>.csproj"/>` ekler.
2. `src/BroidShot/ModuleRegistry.cs` listesine `new BroidShot.Modules.<Ad>.<Ad>Module(),` satırını ekler. Sıra, kenar çubuğundaki sırayı belirler.
3. Modül ve test projesini `BroidShot.slnx` dosyasına ekler (`dotnet sln BroidShot.slnx add ...`).
4. Teslim notunda istenen ortak mesaj türlerini veya Core eklerini uygular.
5. `.\build.ps1 -Verify` ve `.\build.ps1 -Publish` çalıştırır; yeni üçüncü taraf lisanslarını `THIRD_PARTY_NOTICES.md` dosyasına ekler.

Örnek modül (`BroidShot.Modules.Sample`) yalnız kalıbı göstermek için kayıtlıdır; gerçek modüller eklenince kayıttan çıkarılabilir.

**10. Kısa kontrol listesi**

- Arayüz iş parçacığını engelleme; dosya, ağ ve süreç işlerini arka planda yap, ilerlemeyi `host.Jobs` ile göster.
- Kendiliğinden indirme, gönderme veya silme yapma; kullanıcı onayını bildirim düğmesi veya sayfa eylemiyle al.
- Hassas veriyi günlüğe, mesaja veya bildirime koyma.
- Ayar ve verileri yalnız `host.Settings` ve `host.DataFolder` altında tut.
- `Shutdown` içinde abonelikleri, zamanlayıcıları ve dinleyicileri kapat.
- Teslimden önce kendi doğrulama programın geçmeli ve modül projesi uyarısız derlenmeli.
