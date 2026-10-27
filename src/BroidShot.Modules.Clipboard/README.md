**BroidShot.Modules.Clipboard (Pano)**

Kimlik `clipboard`, kategori `ToolCategories.Clipboard`.

Kayıt (birleştirme sırasında):

- `src/BroidShot/BroidShot.csproj`: `<ProjectReference Include="../BroidShot.Modules.Clipboard/BroidShot.Modules.Clipboard.csproj" />`
- `src/BroidShot/ModuleRegistry.cs`: `new BroidShot.Modules.Clipboard.ClipboardModule(),`
- Test: `tests/Clipboard.Verification` (gerçek panoyu kullanan canlı test yalnız `--clipboard-verify` ile çalışır).

Paketler: `Microsoft.Data.Sqlite` 10.0.14 (MIT) → `SQLitePCLRaw.*` 2.1.13 (Apache-2.0), `e_sqlite3` (public domain). DPAPI (`ProtectedData`) WindowsDesktop çatısında hazırdır.

Veri: `<DataFolder>/clipboard.db` (SQLite, `secure_delete` açık; içerik DPAPI ile şifreli blob), `<DataFolder>/images/*.bin` (DPAPI ile şifreli PNG), `<DataFolder>/hash.key` (tekrar algılama için DPAPI korumalı HMAC anahtarı).

Davranış özeti:

- Dinleyici: gizli message-only pencerede `AddClipboardFormatListener`; modülün kendi kopyaları sıra numarası ile yok sayılır.
- Öncelik: dosya listesi > metin (+HTML, düz metin yanında saklanır) > görsel.
- Hassas: kaynak biçimleri (`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory`=0, `Clipboard Viewer Ignore`) veya desenler (JWT, bilinen token önekleri, özel anahtar, `password=`, Luhn kart, IBAN, uzun hex/base64). Saklanır, maskeli gösterilir, yakalamadan 1 saat sonra bellekten ve diskten silinir (30 sn'lik zamanlayıcı + açılışta temizlik). Hassas içerik asla yayınlanmaz ve günlüğe yazılmaz.
- Kara listedeki uygulamaların (işlem adı) kopyaları hiç kaydedilmez.
- URL kopyalanınca `UrlCopiedMessage` yayınlanır (video sitelerinde `LooksLikeVideo = true`); bildirim Medya modülünündür.
- Hızlı panel: **Win+V Windows'un kendi pano geçmişinin yerine BroidShot panelini açar** (varsayılan; Ctrl+Shift+V artık varsayılan değil). Ayarlardaki "Win+V ile hızlı paneli aç" kutusu kapatılınca kanca kaldırılır ve Win+V yine Windows'un paneline gider.
  - Nasıl çalışır: `RegisterHotKey` Win+V'yi alamaz (sistem sahibi), bu yüzden ayrı bir iş parçacığında `WH_KEYBOARD_LL` kancası (`Native/WinVTakeover.cs`) kurulur. Win basılıyken V tuşuna basılınca V yutulur (Windows ve uygulama görmez), panel arayüz iş parçacığına kuyruğa atılarak açılır. Kanca geri çağrısı küçük tutulur (yalnız karar + kuyruğa atma), böylece Windows `LowLevelHooksTimeout` nedeniyle kancayı düşürmez; geri çağrıdan hiçbir istisna dışarı çıkmaz, temsilci alan olarak tutulur (GC toplamaz).
  - Başlat menüsü: V yutulunca sistem Win tuşunun "tek başına" basıldığını sanıp Win bırakılınca Başlat'ı açardı. Bunu önlemek için Win hâlâ basılıyken `SendInput` ile boş bir tuş (VK 0xE8, atanmamış) enjekte edilir; Windows bunu "Win başka bir tuşla kullanıldı" sayar. `LLKHF_INJECTED` olaylar yok sayılır, böylece döngü olmaz (PasteHelper'ın Ctrl+V'si de etkilenmez).
  - Karar mantığı saf ve test edilebilir: `Native/QuickPanelKeyFilter.cs` (tuş olayı + basılı değiştiriciler → `Pass` / `Swallow` / `SwallowAndOpen`). Yalnız tam olarak Win+V yakalanır; Ctrl/Alt/Shift ile birlikte olanlar, Win+A gibi diğer kısayollar ve enjekte edilmiş olaylar geçer. Tekrarlayan V yutulur ama panel bir kez açılır; yutulan V'nin tuş-yukarı olayı da yutulur.
  - Yapıştırma: kanca, Win+V anında öndeki pencereyi alır ve panele hedef olarak verir; Enter yapıştırma `PasteHelper` ile o pencereye döner (değişmedi). Panel açıkken yeniden Win+V paneli kapatır.
  - Ayar göçü: `settings.json` içindeki eski varsayılan `Ctrl+Shift+V` yüklenirken `Win+V`'ye çevrilir; "kapalı" tercihi korunur. `QuickPanelHotkey` başka bir kısayola (örn. `Ctrl+Alt+K`) elle ayarlanırsa o `host.RegisterHotkey` ile kaydedilir, kanca kurulmaz.
  - Sınırlama: Windows, yönetici olarak çalışan (yükseltilmiş) bir pencere öndeyken düşük yetkili bir kancaya tuş iletmez (UIPI); orada Win+V Windows'un kendi panelini açar. BroidShot'u yönetici olarak çalıştırmak bunu çözer ama önerilmez. Kanca kurulamazsa (nadir) ayar sayfasında Türkçe hata gösterilir; panel yine komut paletinden açılır.
  - Test: `Clipboard.Verification` kancayı varsayılan olarak kurmaz; gerçek kanca yalnız `--hook-verify` ile kısa süre kurulup kaldırılır.
