**BroidShot · Windows 11 ekran alıntısı uygulaması**

Siyah arayüz, ince kırmızı vurgular ve yerinde düzenleme. Bu sürüm geliştirme aşamasındaki 0.4 alfasıdır.

**Çalıştırma**

Güncel uygulama `artifacts/BroidShot/BroidShot.exe`, taşınabilir arşiv `artifacts/BroidShot-win-x64.zip` içindedir. Uygulama klasörünün tamamını birlikte taşı; OCR dil dosyaları ve yerel kitaplıklar da uygulamanın yanında bulunmalı. Taşınabilir paket .NET çalışma zamanını içerir.

Geliştirme derlemesini proje içindeki SDK ile çalıştırmak için PowerShell'de:

```powershell
.\.tools\dotnet\dotnet.exe .\src\BroidShot\bin\Release\net10.0-windows10.0.19041.0\BroidShot.dll
```

**Animasyonlu başlatıcı**

Başlatıcı, Windows oturum açılışı dahil her açılışta görünür. Eksik medya araçlarını (yt-dlp / FFmpeg, ilk kurulum yaklaşık 208 MB) resmi kaynaklardan indirip SHA-256 ile doğrular; kurulu araçların güncellemelerini de her açılışta kontrol eder ve yeni sürüm varsa kurar. İndirme ilerlemesi görünür. Atla seçeneği yoktur; hata olursa **Tekrar dene** ile devam edilir, kapatma düğmesi hazırlığı iptal eder. Tamamlanan bileşenler korunur. Uygulama tepside çalışırken yeniden açılırsa yine başlatıcı gösterilir; devam eden iş varsa güncelleme öncesinde bitmesi beklenir. Hazırlıktan sonra ana pencerenin tepside kalması mevcut başlangıç tercihine bağlıdır. OCR dil dosyaları ve .NET taşınabilir pakete dahildir. Windows animasyonları kapalıysa başlatıcı da hareketleri kapatır.

**Araçlar ve komut paleti**

Ana pencerenin sol kenar çubuğu araçları kategorilere göre listeler; ilk araç "Ekran alıntısı" sayfasıdır. Komut paleti her yerden **Ctrl + Alt + K** ile (pencere açıkken **Ctrl + K** ile de) açılır: araç adı veya komut yaz, oklarla seç, Enter ile çalıştır, Escape ile kapat. Kısayol Ayarlar → Kısayol ve çalışma biçimi bölümünden değiştirilebilir veya kapatılabilir. Araçların bildirimleri sağ altta görünür; uzun işler kenar çubuğunun altında ilerleme ve iptal düğmesiyle izlenir. Araç verileri `%LOCALAPPDATA%\BroidShot\modules\`, harici araçlar (yt-dlp, ffmpeg) `%LOCALAPPDATA%\BroidShot\tools\` altında tutulur. Yeni araç geliştirmek için `docs/MODUL_GELISTIRME.md` dosyasına bak.

**Başlangıç ve arka plan**

BroidShot'un kendi sürümü de `Broid07/BroidShot` GitHub Releases üzerinden kontrol edilir. İlk sürüm henüz yayımlanmamışsa bu bilgi gösterilir ve uygulama açılır. Yeni paket SHA-256 ve dosya listesiyle doğrulanır; kurulumda yalnız uygulamaya ait dosyalar değiştirilir. Yeni sürüm açılış kontrolünden geçmezse önceki sürüm geri yüklenir. Kullanıcı ayarları ve geçmiş kurulum klasörünün dışında kalır. Kesilen indirmeler saklanır ve yeniden denemede sürdürülür; bağlantı hatalarında 2, 5 ve 10 saniye aralıklarla otomatik deneme yapılır. Ardından elle yeniden deneme kullanılabilir. Sürüm hazırlama yönergesi `docs/GUNCELLEME_YAYINLAMA.md` içindedir.

Ayarlar → Başlangıç ve arka plan bölümünde:

- **Pencereyi kapatınca arka planda çalışmaya devam et:** açıkken pencere kapanır ve uygulama sistem tepsisinde kalır; F12 ve kısayollar çalışır. Kapalıyken pencereyi kapatmak uygulamayı tamamen sonlandırır.
- **Windows'ta oturum açınca BroidShot'u başlat:** geçerli kullanıcı için Windows başlangıcına ekler veya çıkarır. Varsayılan kapalıdır; yalnız bu seçeneği değiştirdiğinde başlangıç kaydı güncellenir.
- **Windows başlangıcında pencereyi açmadan tepside başlat:** otomatik açılışta ana pencereyi gizler. Arka planda çalışma kapalıysa pencere her zaman görünür açılır. Uygulamayı kendin açtığında normal pencere görünür.

Tepsi menüsündeki **Çıkış** her durumda uygulamayı sonlandırır. Normal bir ikinci açılış mevcut pencereyi gösterir. Otomatik başlangıcın ikinci kez çalışması açık pencereyi öne getirmez. Uygulama klasörünü taşırsan Windows başlangıç seçeneğini kapatıp yeniden açarak kayıtlı konumu güncelle. Windows'un Başlangıç uygulamaları bölümünde ayrıca devre dışı bırakılmış bir kayıt, Windows ayarlarından yeniden etkinleştirilmelidir.

**Yakalama ve düzenleme**

- F12 alan seçimini açar; ayarlardan kapatılabilir. İkincil kısayol ayrıca seçilebilir.
- Alan, pencere, monitör veya tüm ekranlar yakalanabilir.
- Seçimin yanında ok, şekil, kalem, metin, numara, vurgulama ve opak kapatma araçları bulunur.
- Metin çift tıklanarak değiştirilebilir. Nesneler seçilip taşınabilir; Ctrl+Z / Ctrl+Y geri alır ve yineler.
- Ctrl+C son görüntüyü panoya kopyalar; geçmiş açıksa geçmişe ekler. Ctrl+S farklı kaydeder; Escape iptal eder.
- OCR Türkçe/İngilizce metni cihazda işler. OCR metnini kopyalamak ayrı bir eylemdir.

Geçmiş yalnız son görüntüyü saklar; ham masaüstü ve düzenleme katmanları kaydedilmez. Geçmişten tekrar düzenleme, önceki görüntünün üzerine yeni düzenlemeler ekler. Varsayılan saklama sınırı 30 gün / 1 GB'dır.

**Görünüm**

Gövde ve kontroller uygulamaya gömülü Inter 4.1 kullanır; Segoe UI yedek yazı tipidir. Gövde 14px, yardımcı metinler 12–13px, ana başlık 28px; metinler Display/ClearType ile piksele hizalanır. Modül sayfaları ortak koyu tema ve kırmızı vurguları kullanır. Medya sayfasında indirme, geçmiş ve ayarlar ayrı sekmelerdedir. Logo kaynakları `src/BroidShot/Branding/` içindedir.

**Derleme ve doğrulama**

.NET 10 SDK gerekir; proje içindeki `.tools/dotnet` varsa derleme betiği onu kullanır.

```powershell
.\build.ps1
.\build.ps1 -Verify
.\build.ps1 -Publish
```

İlk geri yükleme için NuGet bağlantısı gerekir. Projeler `BroidShot.slnx` çözümünde toplanır; derleme betiği `src/BroidShot.Modules.*` modül projelerini ve `tests/*/*.Verification.csproj` doğrulama programlarını kendiliğinden bulur. `-Verify` kayıt/dışa aktarım/OCR, editör render, pencere yakalama, başlangıç politikaları, gerçek WPF pencere yaşam döngüsü, modül kabuğu (kenar çubuğu, komut paleti, bildirim, iş listesi, hatalı modül yalıtımı) ve Core servislerini (ayar deposu, mesajlaşma, harici araç SHA-256/HTTPS doğrulaması; gerçek ağ kullanılmaz) kontrol eder. Başlangıç testleri gerçek Windows başlangıç kaydını değiştirmez; bellek içi depo kullanır. Masaüstü testi yalnız kendi uygulama pencerelerini oluşturur ve kendi kontrollerini render eder. Panonun bitmap/PNG uyumu için isteğe bağlı `Services.Verification --clipboard-verify` testi panoyu değiştirir.

**Mevcut sınırlar**

HDR tone mapping henüz uygulanmadı. Bazı GPU/korunan uygulamalar doğrudan pencere yakalamayı desteklemeyebilir; o durumda alan seçimi kullanılabilir. Karma DPI/çoklu monitör için daha geniş gerçek donanım doğrulaması gerekiyor. Video/GIF, kaydırmalı alıntı ve gelişmiş görsel sunum araçları sonraki aşamalardır.

OCR üçüncü taraf bildirimleri paket kökündeki `THIRD_PARTY_NOTICES.md` ve `licenses/` klasöründe bulunur. Çalışma planı ve kaynak incelemesi `docs/` içindedir.
