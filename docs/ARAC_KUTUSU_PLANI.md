**BroidShot → günlük araç kutusu planı**

Tarih: 2026-10-27. BroidShot 0.3 alfa ekran alıntısı uygulamasıdır. Bu plan, uygulamayı bilgisayardaki günlük işlerin toplandığı tek bir araç kutusuna genişletir.

**1. Verilen kararlar**

- Erişim: ana pencerede sol kenar çubuğu + global kısayolla açılan komut paleti (araç adı yaz, aç).
- İlk araç grupları: pano yöneticisi, medya indirme, dosya dönüştürme, ekran kaydı / GIF.
- Ek küçük araçlar: renk seçici + ekran cetveli, metin şablonları (snippet), QR + metin araçları, toplu dosya araçları.
- Pano kategorileri: içerik türüne göre otomatik + kullanıcının kendi etiketleri/klasörleri.
- Hassas pano verisi: geçmişte maskeli gösterilir, 1 saat sonra otomatik silinir.
- Video linki kopyalanınca: küçük bildirim ile "İndir" önerilir; otomatik indirme yok.
- Harici araçlar (yt-dlp, ffmpeg): ilk kullanımda resmi kaynaktan indirilir, sonra güncel tutulur. Paket küçük kalır.
- Geliştirme: alt ajanlarla, modüller paralel.

**2. Mimari**

Mevcut tek WPF projesi korunur. Yeni yapı:

| Proje | Sorumluluk |
| --- | --- |
| `BroidShot.Core` | Modül sözleşmeleri (`IToolModule`, `IToolHost`), ortak servisler: bildirim, ayar saklama, veri klasörü, harici araç yöneticisi, iş kuyruğu/ilerleme. UI içermeyen sözleşmeler + ortak WPF stilleri. |
| `BroidShot` (uygulama) | Kabuk: kenar çubuğu, komut paleti, tepsi, kısayollar, mevcut ekran alıntısı. Modülleri kayıt eder. |
| `BroidShot.Modules.Clipboard` | Pano yöneticisi |
| `BroidShot.Modules.Media` | Video/ses indirme |
| `BroidShot.Modules.Convert` | PDF/görsel dönüştürme |
| `BroidShot.Modules.Recorder` | Ekran kaydı / GIF |
| `BroidShot.Modules.Utilities` | Renk seçici, cetvel, snippet, QR, metin araçları, toplu dosya araçları |

Her modül ayrı sınıf kitaplığıdır; yalnız `BroidShot.Core`'a bağlıdır. Böylece modüller paralel geliştirilir ve ayrı derlenir. Uygulama tüm modül projelerine referans verir ve kayıt listesini tutar.

Modül sözleşmesi (taslak):

- `Id`, `Title`, `Category`, `Icon`, `Keywords` (komut paleti araması için)
- `CreateView()` → kenar çubuğunda gösterilen sayfa
- `Commands` → komut paletinde görünen eylemler ("PDF'i PNG'ye çevir", "Panoyu aç")
- `Initialize(IToolHost host)` / `Shutdown()` → arka plan servisleri (pano dinleyici gibi)
- İsteğe bağlı global kısayollar

**3. Modüller**

*Pano yöneticisi*
- `AddClipboardFormatListener` ile dinleme; metin, görsel, dosya listesi, HTML.
- Otomatik tür: link, e-posta, renk kodu, kod, dosya yolu, sayı, düz metin, görsel, dosya.
- Kullanıcı etiketleri/klasörleri, sabitleme, arama, filtre.
- Hızlı panel: Win+V ile imleç yakınında açılan liste (Windows'un kendi pano geçmişinin yerine, ayardan kapatılabilir); Enter ile yapıştırır.
- Hassas veri: `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory=0`, `Clipboard Viewer Ignore` biçimleri ve token/kart/IBAN/JWT benzeri desenler hassas sayılır. Maskeli gösterilir, 1 saat sonra silinir. Uygulama bazlı kara liste.
- Saklama: SQLite + DPAPI (kullanıcı kapsamı) ile şifreli içerik; görseller ayrı dosyada.
- Video linki algılanınca Medya modülüne bildirim önerisi gider.

*Medya indirme*
- yt-dlp + ffmpeg; ilk kullanımda GitHub resmi sürümünden indirilir, SHA-256 doğrulanır, düzenli güncellenir.
- Link yapıştır → bilgi çek (başlık, süre, küçük resim, formatlar) → kalite/format seç (MP4, yalnız ses MP3/M4A) → kuyruk.
- İlerleme, iptal, tekrar dene, çıktı klasörü, playlist desteği.
- İndirilen içeriğin hak sahipliği kullanıcının sorumluluğundadır.

*Dosya dönüştürme*
- PDF → PNG/JPG (sayfa seçimi, DPI): `Windows.Data.Pdf` (sistemde hazır, ek bağımlılık yok).
- Görsel → PDF, PDF birleştir/böl/sayfa çıkar: PdfSharp (MIT).
- Görsel format dönüştürme (PNG/JPG/BMP/TIFF/WebP okuma), yeniden boyutlandırma, sıkıştırma.
- Sürükle-bırak, toplu işlem, Explorer sağ tık "BroidShot ile dönüştür" (sonraki aşama).

*Ekran kaydı / GIF*
- Mevcut alan seçimi katmanıyla alan/pencere/monitör seçimi.
- ffmpeg `ddagrab`/`gdigrab` ile MP4 (H.264); GIF için palette üretimi.
- Başlat/durdur kısayolu, kayıt çerçevesi, isteğe bağlı sistem sesi/mikrofon.

*Küçük araçlar*
- Renk seçici: ekrandan renk al, büyüteç, HEX/RGB/HSL kopyala, renk geçmişi.
- Ekran cetveli: iki nokta arası piksel ölçümü.
- Snippet: kayıtlı metinler; komut paletinden veya kısayolla yapıştırma.
- QR: ekrandan/görselden QR okuma, metin/linkten QR oluşturma (ZXing.Net).
- Metin araçları: JSON biçimlendir, Base64, URL encode, büyük/küçük harf, hash.
- Toplu dosya: toplu yeniden adlandırma (önizlemeli), klasörü türe göre ayırma, zip/unzip.

**4. Uygulama sırası**

1. Çatı: Core projesi, modül sözleşmesi, kenar çubuğu, komut paleti, ortak servisler. Mevcut ekran alıntısı "Ekran alıntısı" modülü olarak kenar çubuğuna taşınır.
2. Paralel modüller: Pano, Medya, Dönüştürme, Küçük araçlar.
3. Ekran kaydı (ffmpeg yöneticisi Medya ile ortak olduğu için 2'den sonra).
4. Birleştirme: modül kayıtları, uçtan uca doğrulama, portable paket.

**5. Kabul ölçütleri**

- Her modül kendi doğrulama programıyla `build.ps1 -Verify` içinde test edilir.
- Komut paleti tüm modül komutlarını bulur; Escape kapatır.
- Pano: hassas işaretli içerik maskelenir ve 1 saat sonra diskten silinir.
- Medya: yt-dlp yokken indirme akışı, bozuk özet değeri reddedilir.
- Dönüştürme: çok sayfalı PDF → PNG sayfa sayısı ve boyutları doğru.
- Mevcut ekran alıntısı davranışı değişmez.
