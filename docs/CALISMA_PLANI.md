**BroidShot — Windows ekran alıntısı uygulaması çalışma planı**

Tarih: 27 Ekim 2026. Durum: araştırma ve geliştirme planı; uygulama henüz yazılmadı.

**1. Senin verdiğin kararlar**

- Önce kişisel kullanım; ileride başkalarıyla paylaşılabilir.
- Windows 11; kullanım sırasında bir veya iki monitör bulunabiliyor.
- İlk sürümün odağı ekran alıntısı, güçlü düzenleme, OCR ve yerel geçmiş.
- Alan seçildikten sonra seçimin yanında araç çubuğu açılacak. Düzenleme aynı yerde yapılacak.
- Düzenlemeyi bitirme eylemi görüntüyü panoya kopyalayacak ve yerel geçmişe ekleyecek. Ayrı dosyaya dışa aktarma isteğe bağlı olacak.
- Yazıyı sonradan değiştirme, ok/numara ekleme, hassas bilgiyi kapatma, arka plan/gölge, kayıt klasörü ve isimlendirme isteniyor.

Geçmişe alma da diske yerel kayıt yapar; “isteğe bağlı dosyaya kaydetme”, kullanıcının seçtiği klasöre ayrıca dışa aktarma anlamına gelir. Geçmişi kapatma ve yalnız panoya kopyalama seçeneği de bulunacak.

**2. Ürün önerisi ve kaynaklardan çıkan karar**

Önerim: BroidShot'u kendi adı, arayüzü ve kod tabanı olan, yerelde çalışan bir Windows uygulaması olarak geliştirmek. Temel deneyim hızlı yakalama ve yerinde düzenleme olacak. Geniş özellik seti aşamalar halinde eklenecek.

| İncelenen proje | Alacağımız tasarım dersi | BroidShot kararı |
| --- | --- | --- |
| [ShareX](https://github.com/ShareX/ShareX) | Zengin yakalama seçenekleri ve yakalama sonrası görevler | Kopyalama, kayıt ve OCR ayrı görevler olacak; ilk sürümde iki basit profil yeterli |
| [Flameshot](https://github.com/flameshot-org/flameshot) | Seçimin yanında düzenleme ve araçların klavyeyle kullanımı | Ana etkileşim bu yaklaşım üzerine kurulacak |
| [Greenshot](https://github.com/greenshot/greenshot) | Düzenlenebilir çizim nesneleri, undo/redo ve ayrı çıktı işlemleri | Yazı ve şekiller kaydetmeden önce değiştirilebilecek |
| [ksnip](https://github.com/ksnip/ksnip) | Yakalama ile annotation katmanlarının ayrılması | Yakalama motoru değiştiğinde editör değişmeyecek |
| [ScreenToGif](https://github.com/NickeManarin/ScreenToGif) | Kayıt ve zaman çizelgesinin bağımsız karmaşıklığı | Video ve GIF ayrı sonraki sürüm olacak |

Bunlar kaynak kod incelemesinden çıkardığımız tasarım kararlarıdır; projelerin hızını veya kullanım kalitesini ölçmüş değiliz. İncelenen dosyalar ve sabit commit bağlantıları [ARAŞTIRMA_KAYNAKLARI.md](ARASTIRMA_KAYNAKLARI.md) dosyasında.

Fork seçeneği açık kalır: bütün kodu GPL koşullarıyla yayımlamak istersen ShareX uyarlaması ayrıca değerlendirilebilir. Şimdiki öneri bağımsız uygulama; ilk kapsamına göre daha küçük bir ürün tasarlamak ve ileride dağıtım kararını esnek tutmak istiyoruz. Bu yolun yakalama ve editör altyapısını kendimizin geliştirmesini gerektirdiği süre tahminine dahildir.

**3. Günlük kullanım akışı**

1. Sistem tepsisindeki uygulama veya kullanıcı tarafından atanmış kısayol ile alıntı başlatılır.
2. Monitör görüntüsü alınır; seçim katmanı açılır. Kullanıcı alan çizer, pencereyi veya tüm ekranı seçer.
3. Seçimin yanında araç çubuğu görünür. Ekran kenarına yaklaşınca çubuk uygun tarafa taşınır.
4. Kullanıcı yazı, ok, numara ve diğer işaretlemeleri ekler. Nesneleri tekrar seçip taşıyabilir, metni değiştirebilir ve geri alabilir.
5. “Bitti / Kopyala” ile son görüntü panoya alınır ve geçmişe kaydedilir. İlk kurulumda bu davranış kısa bir açıklamayla gösterilir.
6. “Farklı kaydet” aynı görüntüyü seçilen klasöre aktarır. OCR metnini kopyalamak ayrı bir eylemdir; yanlışlıkla panodaki görüntüyü değiştirmez.
7. Escape işlemi iptal eder; iptal edilen görüntü geçmişe yazılmaz. Düzenleme başladıysa yanlışlıkla kaybı önleyen kısa bir geri dönüş imkânı tasarlanır.

Pano ve geçmiş sonuçları ayrı izlenir. Örneğin pano başka uygulama tarafından kilitliyse görüntü geçmişte korunur ve “Tekrar kopyala” sunulur. Geçmişe kayıt başarısızsa panoya kopyalama başarısı kaybolmaz; uygulama iki sonucu da doğru bildirir.

**4. Özellik kapsamı**

| Alan | İlk kullanılabilir alfa — v0.1 | Özellik tamamlayan beta — v0.2 / v0.3 | v1.0 sonrası |
| --- | --- | --- | --- |
| Yakalama | Dikdörtgen, tam ekran, seçilen monitör, pencere; kısayol, tepsi, iptal | Son alanı tekrar alma, gecikmeli çekim, imleç seçeneği | Serbest biçim, çoklu alan, otomatik seri çekim |
| Düzenleme | Ok, çizgi, dikdörtgen, elips, kalem, metin, numara, vurgulama; nesne seçme/taşıma/silme; undo/redo | Yeniden boyutlandırma, döndürme, hizalama, renk/kalınlık önayarları | Görsel birleştirme ve daha gelişmiş şablonlar |
| Metin | Türkçe karakterler, yazı tipi/boyut/renk, sonradan düzenleme | Çok satırlı metin ve okunaklı arka plan seçenekleri | Ek tipografi araçları |
| Hassas bilgi | Tam opak kapatma; çıktıdaki piksellere kalıcı uygulama | Bulanıklaştırma ve pikselleştirme görsel efektleri; güvenli kapatma ayrı araç | İsteğe bağlı otomatik hassas alan önerisi |
| Görsel sunum | Kırpma ve temel renkler | Arka plan rengi, boşluk, köşe yuvarlama, gölge; çıktı ölçüsü önizlemesi | Gradyanlar, duvar kâğıdı ve cihaz çerçeveleri |
| Pano/kayıt | Görüntüyü kopyalama, PNG/JPEG, farklı kaydet, hatırlanan klasör | Şablonla dosya adı, kalite ayarı, çakışmada sıralı ad, sürükleyip bırakma | WebP ve toplu dışa aktarma |
| Geçmiş | Temel yerel liste, tekrar kopyalama ve silme | Küçük resimler, tarihe/metne göre arama, favoriler, saklama sınırı | Etiketler ve isteğe bağlı şifreli kasa |
| OCR | Sonraki beta için arayüz noktası | Çevrimdışı Türkçe/İngilizce; seçili alan veya tüm alıntıdan metin; geçmişte arama | Tablo çıkarımı ve çevrimiçi çeviri |
| Yardımcı araçlar | — | Renk seçme; uygunsa ekrana sabitleme | Kaydırmalı alıntı, karşılaştırma, QR okuma |
| Kayıt/paylaşım | — | — | Video/GIF, mikrofon/sistem sesi, kullanıcının seçtiği paylaşım hedefleri |

İlk sürümden kastımız v1.0'dır: OCR ve gelişmiş geçmiş bu sürümde bulunacak. Alfa ve betalar, günlük kullanarak düzeltmemiz için ara teslimlerdir. Video, bulut ve yapay zekâ bu ilk sürümün süresine dahil değildir.

**5. Teknoloji ve mimari önerisi**

Önerilen temel: **C# + .NET 10 LTS + WPF**, Windows 11 x64. ARM64 ayrı paket ve gerçek cihaz testiyle daha sonra ele alınabilir. .NET 10'un destek süresi Kasım 2028'e uzanıyor. Yerel kontrolde yalnız .NET SDK 8.0.425 bulundu; geliştirme başlangıcında .NET 10 SDK kurulumu gerekecek. Bu araştırmada sistemine kurulum yapılmadı. [Microsoft destek politikası](https://dotnet.microsoft.com/en-us/platform/support/policy).

WPF seçimi bir mühendislik önerisi: yalnız Windows hedefinde pencere yönetimi, kısayollar ve seçim katmanı için uygun bir başlangıç sağlıyor. Modern görünüm özel araç çubuğu ve stillerle verilecek. Avalonia, ileride Linux/macOS hedefi oluşursa güçlü aday; WinUI 3 ise Windows görünümü için alternatif. A0 prototipinde WPF seçim katmanı yeterli performans vermezse karar yeniden açılacak. [WPF kaynak deposu](https://github.com/dotnet/wpf).

| Bileşen | Öneri ve sorumluluk |
| --- | --- |
| App | Tek uygulama örneği, sistem tepsisi, ayarlar, kısayollar ve açılış |
| Capture.Windows | Windows Graphics Capture ana aday; SDR sabit görüntüler için ölçülmüş GDI yedek yolu; HDR ve cihaz kaybı açık durumlar |
| Editor.Core | Piksel koordinatlarında belge ve nesne modeli; seçim, metin, ok, şekil, undo/redo; UI bağımsız |
| Editor.Rendering | SkiaSharp adayıyla tek render yolu; araç çubuğundaki görüntü ile dışa aktarımın aynı modeli kullanması |
| Workflow | “Kopyala + geçmiş” ve “dosyaya aktar” işlemlerini sıralar; hataları ve tekrar denemeyi yönetir |
| History | SQLite indeks/metadata; görüntüler dosya sisteminde; küçük resimler ve saklama politikası |
| OCR | Değiştirilebilir IOcrEngine; TR/EN metin, satır ve alan konumları |
| Export | PNG/JPEG, pano biçimleri, klasör ve ad şablonları; atomik dosya yazma |

Bunlar başlangıçta aynı uygulama içindeki modüller olacak; her biri için ayrı servis veya süreç açılması gerekmiyor. Eklenti sistemi ve görsel iş akışı tasarımcısı ilk sürümde gerekmiyor.

Yakalama prototipinde Win32 `CreateForMonitor` / `CreateForWindow` yolu ve destek kontrolü değerlendirilecek; sistem seçim penceresine zorunlu bağımlılık varsayılmayacak. Kaynaklar UI iş parçacığı dışında işlenecek. Önce monitör kareleri alınacak, sonra seçim katmanı gösterilecek; böylece katman kendi görüntüsüne karışmayacak. [Microsoft Win32 capture örneği](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/tree/master/cpp/ScreenCaptureforHWND).

HDR çıktısında ilk hedef doğru görünen SDR PNG/JPEG olacak. Float yakalama ve tone mapping prototipte doğrulanacak; HDR dosya dışa aktarımı sonraki iş. [Microsoft capture ve HDR açıklaması](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture).

OCR için taşınabilir kullanımda **Tesseract + Türkçe/İngilizce dil verileri** başlangıç adayı. Model dosyaları sürümü ve bütünlüğüyle paketlenir; kullanıcı alıntıları dışarı gönderilmez. MSIX tercih edilirse Windows OCR ikinci adapter olabilir: resmi belgede Windows.Media.Ocr masaüstünde paket kimliği istiyor. Böylece portable pakette aynı API'nin mutlaka çalışacağı varsayılmayacak. [Tesseract](https://github.com/tesseract-ocr/tesseract), [dil modelleri](https://github.com/tesseract-ocr/tessdata_fast), [Windows OCR koşulu](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr).

**6. Düzenleme ve verilerin davranışı**

Eklenen yazı ve çizimler nesne olarak tutulacak. Kullanıcı metne çift tıklayıp yeniden yazabilecek, okun yönünü değiştirebilecek ve numarayı taşıyabilecek. Orijinal ekrandaki mevcut yazıyı belge metni gibi değiştirmek farklı bir özelliktir: ilk sürümde OCR metni çıkarılır; ekran görüntüsündeki yazı otomatik olarak düzenlenebilir metne dönüştürülmez.

Normal alıntıda geçmiş, yeniden düzenlenebilir belgeyi ve son görüntüyü saklayabilir. “Hassas bilgiyi kalıcı kapat” işlemi tamamlanınca kapalı alanlar kaynak görüntüye de uygulanır; maskenin arkasındaki pikseller, eski belge sürümleri, küçük resimler ve OCR indeksinde korunmaz. Bu işlem oturum açıkken undo ile geri alınabilir; kalıcı kayıt sonrasında kapatılmış alanın eski içeriği geri getirilemez. Bu sınır arayüzde anlaşılır olacak. Bulanıklık güvenli silme olarak sunulmayacak.

İlk kurulumda önerilen geçmiş sınırı 30 gün veya 1 GB; kullanıcı değiştirebilir ya da geçmişi kapatabilir. Favoriler bu sınıra dahil mi seçeneği açıkça gösterilecek. Görseller normal Windows kullanıcı profilinde tutulacak; ilk sürüm şifreli kasa iddiasında bulunmayacak. Disk dolması, yarım yazma ve veritabanı/görsel tutarsızlığı için onarım davranışı tanımlanacak.

Dosya adı örneği `BroidShot_2026-10-27_21-45-08_001.png`; tarih/saat yerel kullanıcı ayarından gelir. Başlıkla adlandırma isteğe bağlıdır; varsayılan olarak pencere başlığındaki özel bilgiler dosya adına taşınmaz.

**7. Geliştirme aşamaları ve teslim ölçütleri**

Tahmin, Windows masaüstü geliştirmesinde deneyimli bir kişinin tam zamanlı çalışması ve düzenli senin geri bildirimin varsayımıyla hazırlanmıştır. Aşağıdaki süreler iş günüdür; kesin teslim taahhüdü değildir.

| Aşama | Süre | Somut teslim | Tamamlandı sayılması için |
| --- | --- | --- | --- |
| A0 — Teknik doğrulama | 3–5 gün | Yakalama/overlay denemesi, UI taslağı, mimari kararı | 1/2 monitörde farklı DPI, koordinatlar, HDR denemesi; pano ve render prototipi; risklerin raporu |
| A1 — Yakalama çekirdeği | 5–8 gün | Tepsi, kısayol, bölge/pencere/monitör seçimi, temel çıktı | İptal temiz; monitör değişince çökme yok; doğru alan panoya ve dosyaya gider |
| A2 — Yerinde düzenleyici | 10–15 gün | Araç çubuğu, düzenlenebilir nesneler, undo/redo, kapatma, temel geçmiş; v0.1 | Metin tekrar düzenlenir; pano ve dosya aynı sonucu üretir; hassas alan çıktıda geri açılamaz |
| A3 — OCR, geçmiş ve çıktı | 5–8 gün | TR/EN OCR, arama, saklama sınırı, arka plan/gölge, adlandırma; v0.2/v0.3 | Çevrimdışı işler; dosya ve pano hataları anlaşılır; yeniden başlatmada geçmiş sağlam |
| A4 — Günlük kullanım ve v1.0 | 8–12 gün | Hata düzeltmeleri, erişilebilirlik, paket, sürüm notu, gerçek kullanım denemesi | Aşağıdaki kritik testler geçer; senin örnek akışların tamamlanır; temiz makinede paket çalışır |

Temel toplam 31–48 iş günü. Yaklaşık %20 belirsizlik payıyla 38–58 iş günü, yani **8–12 tam zamanlı hafta** planlanmalı. İlk kullanılabilir alfa yaklaşık 4–6 haftada hedeflenir. Yarı zamanlı geliştirmede takvim haftaları artar; AI/alt ajan kullanımı bu süreyi otomatik olarak kısaltma garantisi değildir. A0 sonunda ölçümlere göre tahmin güncellenir.

A1, A0 kararlarına; A2, yakalama/piksel modeline; A3, belge ve kalıcı kayıt modeline bağlıdır. A4'te özellik eklemek yerine kabul ölçütlerini tamamlarız. A0'daki temel yakalama veya DPI doğrulanamazsa editörü genişletmeden o engel çözülür.

**8. İlk iş listesi ve kabul senaryoları**

| Öncelik | İş | Kontrol örneği |
| --- | --- | --- |
| P0 | Monitör/piksel koordinat modeli | Soldaki monitör negatif koordinatta, sağdaki %150; seçim kaymaz |
| P0 | Yakalama oturumunun ömrü | Escape, yeni yakalama, monitör çıkarma ve kilitle/aç sonrası kaynaklar serbest kalır |
| P0 | Nesne modeli | Metin ekle → ok ekle → metni değiştir → geri al/ileri al; sonuç tutarlı |
| P0 | Ortak çıktı üretimi | Aynı belgede pano PNG'si ve kaydedilmiş PNG aynı boyut/piksel sonucunu verir |
| P0 | Tam opak kapatma | Dışa aktarım, geçmiş, OCR ve küçük resimde kapalı içeriğin eski sürümü bulunmaz |
| P0 | Bitirme görevi | Panoya kopyalama başarılı/geçmiş başarısız ve tersi durumlar ayrı doğru gösterilir |
| P1 | Dosya dışa aktarımı | İsim çakışması, salt okunur klasör, disk dolması; mevcut dosya yanlışlıkla kaybolmaz |
| P1 | OCR doğruluğu | TR/EN referans görüntülerinde İ/ı/ş/ğ, küçük metin, açık/koyu tema ve satır sonları ölçülür |
| P1 | Geçmiş | Yeniden başlat, ara, tekrar kopyala, sil, süre/boyut sınırı ve Türkçe arama çalışır |
| P1 | Erişilebilirlik | Klavyeyle seçim ve araçlara erişim; görünür odak ve okunaklı araç etiketleri |

Anlamlı otomasyon: koordinat dönüşümleri, undo/redo, çıktı render karşılaştırmaları, kayıt/onarım ve kapatma senaryoları. Gerçek Windows oturumu gerektiren kısayol, clipboard hedefleri, GPU/HDR ve monitör takıp çıkarma ayrıca elle doğrulanır.

Test matrisi: tek ekran %100/%125/%150/%200; iki ekran karışık DPI; ekranların solda/üstte olması; dikey ekran; monitör çıkarma; uyku/uyanma; HDR açık/kapalı erişilebilen donanımda. Pano Word, tarayıcı, Paint ve günlük kullandığın mesajlaşma uygulamasıyla denenir. Korunan içerik, küçültülmüş pencereler ve güvenli masaüstü için evrensel yakalama sözü verilmez; uygulama başarısızlığı anlaşılır bildirir.

Ön performans hedefleri: belirlenmiş referans cihazda sıcak uygulamada kısayoldan seçim katmanına P95 ≤300 ms; 1080p alıntıda bitirmeden pano/geçmiş sonucuna P95 ≤500 ms; OCR UI'yi dondurmaz. Bunlar ölçülmüş sonuç değildir; A0'da uygulanabilirlikleri ve referans cihaz tanımı netleşecek. En az 30 tekrar üzerinden ölçüm tutulur; 100 alıntıdan sonra belleğin sürekli büyümemesi ayrıca izlenir.

**9. Sonraki sürümler**

- Ekrana sabitleme: üstte tutma, saydamlık, boyut, geçici fare geçişi.
- Kaydırmalı alıntı: önce seçili tarayıcı ve desteklenen uygulama listesi; sabit başlıklar, animasyonlar ve sonsuz sayfalar için bitirme/sınır koşulu. Her uygulamada çalışacağı varsayılmaz.
- Video/GIF: ayrı kayıt durumu, durdurma/iptal, mikrofon/sistem sesi, codec ve lisans incelemesi; Media Foundation/FFmpeg seçimi bu aşamada yapılır.
- Paylaşım: kullanıcı seçimiyle hedef ve kimlik bilgisi yönetimi. Otomatik bulut yükleme varsayılan olmaz.
- AI/çeviri: isteğe bağlı; hangi içerik nereye gönderiliyor ve maliyeti açıkça gösterilir.

**10. Lisans, dağıtım ve çalışma düzeni**

ShareX, Flameshot ve Greenshot GPL ailesindeki lisanslarla dağıtılıyor; ksnip ana uygulaması ile kImageAnnotator kütüphanesinin lisansları ayrı incelenmeli. Kaynak okumak ve tasarım fikirlerinden yararlanmak ile kodu kopyalayıp dağıtmak ayrı kararlardır. GPL bir projeyi kişisel olarak değiştirip kullanmak kaynakları herkese yayımlama zorunluluğu doğurmaz; değiştirilmiş sürümü dağıtmak ilgili kaynak ve lisans yükümlülüklerini getirir. Ücretli satış da tek başına GPL ile yasak değildir. [GNU resmi açıklaması](https://www.gnu.org/licenses/gpl-faq.en.html#GPLRequireSourcePostedPublic).

Bağımsız uygulamada kullanılacak NuGet paketleri, native DLL'ler, ikonlar, yazı tipleri ve OCR model verileri için sürüm/lisans envanteri tutulacak. İlk kişisel paket portable/self-contained x64 olabilir; dağıtım öncesi temiz Windows testi, üçüncü taraf bildirimleri ve imzalama değerlendirilir. MSIX + Windows OCR seçilirse paket kimliği ve kurulum yolu buna göre değiştirilir. Ücretli sertifika/servis alımı bu planın parçası olarak yapılmadı.

Geliştirme başlayınca her aşama çalışır bir uygulama ve kısa doğrulama notuyla teslim edilir. Alt ajanlar capture/DPI, editör ve test incelemesi gibi sınırları belli işlerde kullanılabilir; ortak belge modeli ana ajan tarafından korunur. Her teslimde senin günlük akışınla “alıntı al, yazı/ok ekle, kapat, kopyala, geçmişten bul, farklı kaydet” denenir.

Başlangıç için eksik bir ürün kararı kalmadı. Görsel tema, kısayol ve kayıt klasörü prototipte değiştirilebilir tercihler olarak sunulabilir. İlk uygulanacak adım A0'dır.
