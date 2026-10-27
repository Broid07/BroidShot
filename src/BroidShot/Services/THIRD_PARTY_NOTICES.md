**BroidShot üçüncü taraf bileşenleri / Third-party notices**

Bu dosya ve `licenses/` dizini build ve publish çıktısına birlikte kopyalanır. Aşağıdaki OCR bileşenleri değiştirilmeden kullanılır. BroidShot uygulama kodu bağımsız olarak yazılmıştır; araştırılan ShareX, Flameshot, Greenshot ve ksnip projelerinden kaynak kod kopyalanmamıştır.

| Bileşen | Sürüm / kaynak | Lisans ve paketlenen metin |
| --- | --- | --- |
| Tesseract .NET wrapper | NuGet Tesseract 5.2.0, Charles Weld | Apache-2.0; [tam lisans](licenses/Apache-2.0.txt), [orijinal README ve telif bildirimi](licenses/Tesseract-wrapper-ReadMe.md) |
| Tesseract native OCR engine | Paket içindeki `tesseract50.dll`; upstream 5.2.0 | Apache-2.0; [tam lisans](licenses/Tesseract-native-LICENSE.txt) |
| Leptonica | Paket içindeki `leptonica-1.82.0.dll` | BSD 2-clause; [orijinal telif, koşullar ve sorumluluk reddi](licenses/Leptonica-BSD.txt) |
| InteropDotNet | Wrapper'ın içerdiği dinamik interop kodu; Andrey Akinshin | MIT; [orijinal lisans](licenses/InteropDotNet-MIT.txt) |
| tessdata_fast Türkçe ve İngilizce modelleri | `87416418657359cb625c412a48b6e1d6d41c29bd` | Apache-2.0; [tam lisans](tessdata/LICENSE), [kaynak ve SHA256](tessdata/SOURCE.txt) |

Tesseract wrapper README telif bildirimi: Copyright 2012–2019 Charles Weld. NuGet 5.2.0 paket metadata'sında Copyright 2012–2020 Charles Weld bildirimi de bulunur; ikisi burada korunmuştur. InteropDotNet: Copyright (c) 2014 Andrey Akinshin. Leptonica telif bildirimi özgün lisans dosyasında korunmuştur.

Resmi kaynaklar:

- Wrapper [5.2.0 etiketi / commit 2c993543f7fa66576a8890a6c4ab053c4598aaed](https://github.com/charlesw/tesseract/tree/2c993543f7fa66576a8890a6c4ab053c4598aaed). README lisans ve InteropDotNet bildirimlerini içerir. Aynı etiketin ChangeLog'u native motorun Tesseract 5.2'ye yükseltildiğini belirtir; `tesseract50.dll` dosya adı ABI adıdır.
- Native Tesseract [5.2.0 / commit 5ad5325a0aa8effc47ca033625b6a51682f82767](https://github.com/tesseract-ocr/tesseract/tree/5ad5325a0aa8effc47ca033625b6a51682f82767).
- Leptonica [1.82.0 / commit f4138265b390f1921b9891d6669674d3157887d8](https://github.com/DanBloomberg/leptonica/tree/f4138265b390f1921b9891d6669674d3157887d8).
- InteropDotNet [lisans snapshot'ı 0b15eee809716c50562458d6fe95d52bea46b9c6](https://github.com/AndreyAkinshin/InteropDotNet/blob/0b15eee809716c50562458d6fe95d52bea46b9c6/LICENSE.md).
- Apache Software Foundation [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0.txt).

Lisans dosyaları resmi kaynaklardan 27 Ekim 2026 tarihinde alındı; indirme adresleri ve hash'leri [licenses/SOURCES.txt](licenses/SOURCES.txt) içinde bulunur. Microsoft .NET/WPF çalışma zamanı self-contained pakete dahil edilirse Microsoft tarafından sağlanan lisans ve üçüncü taraf bildirimleri de dağıtımda korunur. Visual C++ çalışma zamanı ayrı bir sistem önkoşuludur; bu proje onun kurulum paketini yeniden dağıtmaz.

Bu envanter doğrudan OCR bileşenlerini ve wrapper'ın bildirdiği gömülü InteropDotNet kodunu kapsar. NuGet tarafından sağlanan native DLL'lerin ikili dosyalarından tüm statik codec bağımlılıklarının eksiksiz kaynak eşlemesi çıkarılmış değildir; kamuya dağıtım öncesi native build içeriği ve ek codec bildirimleri ayrıca doğrulanmalıdır.
