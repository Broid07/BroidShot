# BroidShot sürüm hazırlama

Güncelleme kaynağı `https://github.com/Broid07/BroidShot` deposunun yayımlanmış, taslak olmayan son kararlı sürümüdür. İlk sürüm bulunmadığında launcher bunu belirtir ve mevcut uygulamayla devam eder. Uygulama paketine hiçbir GitHub erişim anahtarı eklenmez.

## Yerel paket

PowerShell'de proje kökünden:

```powershell
.\publish.ps1 -Version 0.4.0 -Verify
```

Betik Core ve Updates doğrulamalarını çalıştırır, Windows x64 paketini .NET dahil üretir ve şu çıktıları hazırlar:

- `artifacts/BroidShot/`: çalıştırılabilir uygulama klasörü.
- `artifacts/BroidShot-win-x64.zip`: içeriği klasör eklenmeden kökte başlayan sürüm paketi.
- `artifacts/BroidShot-win-x64.zip.sha256`: arşivin doğrulama özeti.
- Paket içindeki `BroidShot.package.json`: sürüm ve uygulamaya ait her dosyanın SHA-256 özeti.

Yeni sürümde daha yüksek üç parçalı bir numara kullanın; örneğin `0.4.1`. Paket sürümüyle GitHub etiketi eşleşmelidir (`v0.4.1`). Paket içindeki manifest dosyasını veya paket dosyalarını sonradan değiştirmeyin; değişirse betikle yeniden paketleyin.

## GitHub'da yayımlama

Yerel paket hazırlanması GitHub'a dosya yüklemez. Depodaki Windows release iş akışı `v0.4.0` gibi bir etiket gönderildiğinde veya GitHub Actions üzerinden elle çalıştırıldığında doğrulama ve paketleme yapıp ZIP ile `.sha256` dosyalarını yayımlar. Normal geliştirme commitleri yayın başlatmaz. Aynı sürümün paketi üzerine yazılmaz; sonraki yayın için numara yükseltilmelidir. Paket adı her sürümde `BroidShot-win-x64.zip` olarak kalır.

## Kurulum ve geri dönüş

Launcher önce araçları ve uygulama sürümünü kontrol eder. Yeni uygulama paketi indirilip doğrulanır; yeni paketin içindeki aynı uygulama çalıştırıcısı yardımcı kipte kurulum yapar, fazladan bir updater EXE dağıtılmaz. Çalışan uygulamanın kapanması beklenir. Önceki dosyalar `%LOCALAPPDATA%/BroidShot/updates/previous` altında tek kopya tutulur. Yalnız eski/yeni manifestte listelenen program dosyaları değişir; kullanıcının eklediği diğer dosyalar korunur.

Yeni süreç ana pencere oturumunu başlatıp sağlık işaretini yazmalıdır. Başlatılamazsa veya kontrol süresi aşılırsa eski dosyalar geri yüklenir. Başarısız sürüm tekrar tekrar kurulmaz; daha yeni bir GitHub sürümü beklenir. Güncelleme önbelleği ve günlükleri `%LOCALAPPDATA%/BroidShot/updates` içindedir. Pano, alıntı geçmişi ve ayarlar aynı kullanıcı veri kökünde korunur.

İndirme devamı HTTP Range desteğini kullanır. Sunucu aralığı desteklemezse dosya baştan alınır; her iki durumda da tam dosyanın SHA-256 özeti doğrulanmadan kurulum yapılmaz. Özetle uyuşmayan kısmi dosya silinir.

## Doğrulama sınırı

Yerel testler sahte GitHub/HTTP yanıtları ve geçici kurulum klasörleriyle sürüm seçimini, indirmeyi sürdürmeyi, doğrulamayı, kurulum geri dönüşünü ve kullanıcı dosyalarının korunmasını kontrol eder. İlk gerçek GitHub sürümü yayımlandıktan sonra iki farklı gerçek sürüm arasında uçtan uca dağıtım denemesi ayrıca yapılmalıdır.
