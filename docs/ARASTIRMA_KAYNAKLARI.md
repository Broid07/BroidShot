**BroidShot — açık kaynak ve Windows API araştırma notları**

27 Ekim 2026. İnceleme yöntemi: resmi depolar, seçilmiş kaynak dosyalarının statik okunması ve Microsoft belgeleri. Projeler derlenmedi, kurularak karşılaştırılmadı veya performans testine alınmadı. Özellik listesi, kod bulgusu ve BroidShot için öneri birbirinden ayrılmıştır.

Geliştirme dalı commit kimlikleri `git ls-remote` ile doğrulandı. Aşağıda özellikle belirtilen temel dosyalar sabit commit'in ham içeriğinden tekrar okundu. Diğer ek dosyalar dalın web görünümünden incelendi; bütün deponun aynı snapshot üzerinde ayrıntılı denetimi yapılmadı. GitHub web önbelleği bazı eski Greenshot yolları sunduğu için ana mimari bulgular sabit commit dosyalarıyla kontrol edildi.

**ShareX**

Geliştirme snapshot: `fdb0d415f7124b0f95e69e4c375cd6a2a25bf041`.

- [Uygulama proje dosyası](https://github.com/ShareX/ShareX/blob/fdb0d415f7124b0f95e69e4c375cd6a2a25bf041/ShareX/ShareX.csproj): .NET 10, Avalonia ve SkiaSharp. [Editör projesi](https://github.com/ShareX/ShareX/blob/fdb0d415f7124b0f95e69e4c375cd6a2a25bf041/ShareX.ImageEditor/ShareX.ImageEditor.csproj) ayrı bileşen.
- [Screenshot.cs](https://github.com/ShareX/ShareX/blob/fdb0d415f7124b0f95e69e4c375cd6a2a25bf041/ShareX.ScreenCaptureLib/Screenshot.cs): GDI tabanlı sabit görüntü, alan/pencere/ekran ve imleç davranışları.
- [HDRScreenCapture.cs](https://github.com/ShareX/ShareX/blob/fdb0d415f7124b0f95e69e4c375cd6a2a25bf041/ShareX.ScreenCaptureLib/HDRScreenCapture.cs): DXGI ve tone mapping ile ayrı HDR yolu.
- [TaskManager.cs](https://github.com/ShareX/ShareX/blob/fdb0d415f7124b0f95e69e4c375cd6a2a25bf041/ShareX/TaskManager.cs): görev durumu, yükleme eşzamanlılığı ve geçmiş koordinasyonu.

Karşılaştırma için [v21.0.0 proje snapshot'ı](https://github.com/ShareX/ShareX/blob/d2502561f63fc3ff502cacd91514e3f7f2948c74/ShareX/ShareX.csproj) .NET 9/WinForms kullanıyor. Geliştirme dalındaki dönüşüm bütün yayımlanmış sürümlere genellenmemeli. [Resmi özellik listesi](https://github.com/ShareX/ShareX) OCR, kaydırmalı yakalama ve iş akışları için ayrıca incelendi.

Çıkarım: geniş kapsam için güçlü referans; ilk BroidShot'ta basit profil ve ayrı çıktı görevleri uygun. Mevcut uygulamanın bütün ayar yapısını kopyalamak gerekmiyor.

**Greenshot**

Geliştirme snapshot: `a3dbb015fdda52c2ad7f781173b955f445c8a81e`.

- [Directory.Build.props](https://github.com/greenshot/greenshot/blob/a3dbb015fdda52c2ad7f781173b955f445c8a81e/src/Directory.Build.props): net480, WPF/WinForms hibrit yapı ve GPL-3.0-or-later bildirimi.
- [WindowsGraphicsCaptureInterop.cs](https://github.com/greenshot/greenshot/blob/a3dbb015fdda52c2ad7f781173b955f445c8a81e/src/Greenshot.Base/Native/WindowsGraphicsCaptureInterop.cs): asenkron yakalama, iptal, eşzamanlılık kontrolü, D3D cihazı ve HDR işleme.
- [CapturePipeline.cs](https://github.com/greenshot/greenshot/blob/a3dbb015fdda52c2ad7f781173b955f445c8a81e/src/Greenshot/Recipes/Pipeline/CapturePipeline.cs): yakalama ve çıktı adımlarını ayıran pipeline.
- [Surface.cs](https://github.com/greenshot/greenshot/blob/a3dbb015fdda52c2ad7f781173b955f445c8a81e/src/Greenshot.Editor/Drawing/Surface.cs): nesne seçimi, DPI/zoom ve memento tabanlı geri alma.

Bu dosyalar sabit commit üzerinden doğrulandı. Çıkarım: nesne tabanlı editör ve ayrı çıktı akışı yararlı; yeni uygulamada eski çalışma zamanı ve bütün DAG motorunu taşımak gerekmiyor.

**Flameshot**

Geliştirme snapshot: `da34dcb1b296bd2e3bc589767abbe788084215ae`.

- [CMakeLists.txt](https://github.com/flameshot-org/flameshot/blob/da34dcb1b296bd2e3bc589767abbe788084215ae/CMakeLists.txt): C++20 ve Qt6.
- [capturewidget.cpp](https://github.com/flameshot-org/flameshot/blob/da34dcb1b296bd2e3bc589767abbe788084215ae/src/widgets/capture/capturewidget.cpp): seçim, çizim araçları, klavye, undo yığını ve Windows ölçek/monitör dönüşümleri.

Bu iki dosya sabit commit üzerinden doğrulandı. Ek dal incelemesi: `src/widgets/capture/modificationcommand.cpp` ve `src/tools/pin/pintool.cpp`; komut geçmişi ve pin görevi. [README lisans bölümü](https://github.com/flameshot-org/flameshot#license) ana kod, ikon ve logoların lisanslarını ayırıyor.

Çıkarım: kullanıcının istediği seçim yanında düzenleme akışının en doğrudan referansı. BroidShot'ta seçim penceresi, belge modeli ve çıktı görevleri daha küçük bileşenlere ayrılabilir. Bu değerlendirme performans veya kullanılabilirlik ölçümü değildir.

**ksnip ve kImageAnnotator**

ksnip snapshot: `059f022cb9c4bdd30d3e8311eb9199ca92fed4f3`.

- [CMakeLists.txt](https://github.com/ksnip/ksnip/blob/059f022cb9c4bdd30d3e8311eb9199ca92fed4f3/CMakeLists.txt): C++17; Qt5.15.2 temeli ve isteğe bağlı Qt6 yolu.
- [KImageAnnotatorAdapter.cpp](https://github.com/ksnip/ksnip/blob/059f022cb9c4bdd30d3e8311eb9199ca92fed4f3/src/gui/imageAnnotator/KImageAnnotatorAdapter.cpp): uygulama ile annotation kütüphanesinin sınırı; yükleme, sekmeler, kırpma, ölçek ve undo/redo.

Bu iki dosya sabit commit üzerinden doğrulandı. Ek dal incelemesi: `src/CMakeLists.txt`, `WinImageGrabber.cpp` ve `PinWindow.cpp`; yakalama modları ve bağımsız pin penceresi.

[ksnip LICENSE.txt](https://github.com/ksnip/ksnip/blob/059f022cb9c4bdd30d3e8311eb9199ca92fed4f3/LICENSE.txt) GPLv3; bazı dosya başlıkları GPLv2-or-later. Ayrı [kImageAnnotator LICENSE](https://github.com/ksnip/kImageAnnotator/blob/ba5692e5c921026b7cf4052657744c6ab08a528d/LICENSE) LGPLv3'tür. Kütüphane commit kimliği doğrulandı; lisans metni dal görünümünden okundu. Bu ayrım ksnip uygulama kodunun LGPL olduğu anlamına gelmez.

Çıkarım: capture, annotation ve pin bileşenlerinin ayrılması BroidShot için değerli. İlk sürümde sekmeli tam editör zorunlu değil.

**ScreenToGif**

Geliştirme snapshot: `a4d0a67c2131cd048ceec86cd40afc2f1a06f2fd`.

- [ScreenToGif.csproj](https://github.com/NickeManarin/ScreenToGif/blob/a4d0a67c2131cd048ceec86cd40afc2f1a06f2fd/ScreenToGif/ScreenToGif.csproj): .NET 9/WPF ve SharpDX.Direct3D11 referansları.
- [DirectImageCapture.cs](https://github.com/NickeManarin/ScreenToGif/blob/a4d0a67c2131cd048ceec86cd40afc2f1a06f2fd/ScreenToGif/Capture/DirectImageCapture.cs): Desktop Duplication, GPU kaynakları, değişen/taşınan bölgeler, dönüş, imleç ve cihaz sıfırlanması.

Bu dosyalar sabit commit üzerinden okundu. Ek dal incelemesi: `Recorder.xaml.cs`, DPI/ekran ve güç değişimi olayları. Depo [MS-PL lisansını](https://github.com/NickeManarin/ScreenToGif/blob/a4d0a67c2131cd048ceec86cd40afc2f1a06f2fd/LICENSE.txt) kullanıyor; dağıtımda dosya ve bağımlılık lisansları ayrıca kontrol edilmeli.

Çıkarım: video/GIF için sonraki araştırma kaynağı. Burada kullanılan eski bağımlılıkları yeni projeye otomatik seçmiyoruz.

**Windows API ve destek belgeleri**

| Birincil kaynak | Planı etkileyen bulgu |
| --- | --- |
| [Screen capture](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture) | WGC pencere/ekran kareleri, destek kontrolü, HDR float pipeline/tone mapping ve cihaz kaybı |
| [CreateForMonitor](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createformonitor) | Win32 monitör handle'ından capture item oluşturma; Windows 10 1903+ API |
| [Win32 capture örneği](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/tree/master/cpp/ScreenCaptureforHWND) | Win32 entegrasyonu; küçültülmüş pencere sınırlaması |
| [Desktop Duplication](https://learn.microsoft.com/en-ie/windows-hardware/drivers/display/desktop-duplication-api) | GPU belleğinde kareler, monitör sınırları ve korunan içerik |
| [DPI awareness ayarı](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process) | DPI farkındalığını manifest ile erken tanımlama; PerMonitorV2 değerlendirmesi |
| [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey) | Kısayol kaydı başarısız olabilir; sistem ve uygulama çakışmalarını yönetme |
| [SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity) | Kendi kontrol pencerelerini capture dışında tutma adayı; mutlak içerik koruması garantisi değil |
| [Windows.Media.Ocr](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr) | Masaüstünde paket kimliği gereksinimi; portable ile MSIX kararını etkiler |
| [.NET destek politikası](https://dotnet.microsoft.com/en-us/platform/support/policy) | .NET 10 LTS, destek sonu 14 Kasım 2028 |
| [WPF](https://github.com/dotnet/wpf), [SkiaSharp](https://github.com/mono/SkiaSharp) | Windows arayüzü ve ortak render katmanı adayları |
| [Tesseract](https://github.com/tesseract-ocr/tesseract), [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) | Yerel OCR motoru ve ayrı dil verileri; motor/model/yerel bağımlılık envanteri gerekir |
| [GNU GPL SSS](https://www.gnu.org/licenses/gpl-faq.en.html#GPLRequireSourcePostedPublic) | Kişisel değişiklik ile değiştirilmiş programı dağıtmanın farklı yükümlülükleri |

Bu liste bir tam güvenlik veya lisans denetimi değildir. Uygulama geliştirilirken gerçekten seçilen bağımlılıkların sürümleri sabitlenecek, lisans metinleri ve paket içerikleri üzerinden kontrol edilecek. Teknoloji ve süre önerileri kaynaklardan yaptığımız mühendislik çıkarımlarıdır.
