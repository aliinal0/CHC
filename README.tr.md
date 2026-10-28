<p align="center">
  <img src="src/CHC.App/Assets/chc.png" width="96" alt="CHC logosu">
</p>

<h1 align="center">CHC – Computer Health Check</h1>

<p align="center">
  Bilgisayarınızın sağlığını (işlemci, bellek, diskler, ekran kartı, pil, anakart ve sistem kararlılığı) kontrol eden ve sonuçları sade bir özetten ham teknik verilere kadar açıklayan ücretsiz bir Windows uygulaması.
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <b>Türkçe</b> ·
  <a href="README.de.md">Deutsch</a> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.pt-BR.md">Português (Brasil)</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.zh-CN.md">简体中文</a>
</p>

## Özellikler

- Bilgisayarın tamamı ve her bileşen için **0–100 arası sağlık puanı**, *Sorunsuz / İpucu / Uyarı / Kritik* durumlarıyla.
- **Sorunlar ve öneriler.** Her bulgu neyin yanlış olduğunu, neden önemli olduğunu ve ne yapmanız gerektiğini anlatır.
- **Basitten teknik ayrıntıya.** Her bileşen sayfası kısa bir özetle başlar; ardından canlı değerler, teknik özellikler, tablolar (bellek modülleri, SMART öznitelikleri, olay günlüğü) ve tüm ham sensörler gelir.
- **İşlemci:** yük, sıcaklık, saat hızı, güç, çekirdek/iş parçacığı, önbellek, sanallaştırma.
- **Bellek:** kullanım, ayrılmış bellek, yuva bazında modüller (tür, hız, parça numarası), tek kanal ve XMP/EXPO tespiti.
- **Depolama:** Windows sağlık durumu ve tam SMART verisi (CrystalDiskInfo ile aynı motor, DiskInfoToolkit üzerinden): kalan ömür, sıcaklık, çalışma saati, toplam yazılan veri, yeniden atanmış/bekleyen/düzeltilemeyen sektörler, NVMe kritik uyarıları ve sürücü başına boş alan.
- **Ekran kartı:** sıcaklık, sıcak nokta, bellek sıcaklığı, yük, güç, VRAM, sürücü sürümü ve yaşı.
- **Pil** (dizüstü): yıpranma oranı, tasarım ve tam şarj kapasitesi, şarj döngüsü.
- **Anakart ve sistem:** BIOS/UEFI sürümü ve yaşı, Güvenli Önyükleme, TPM, Windows sürümü ve açık kalma süresi.
- **Kararlılık:** son 30 gündeki beklenmedik kapanmalar, mavi ekranlar, WHEA donanım hataları, disk hataları ve Aygıt Yöneticisi sorunları.
- LibreHardwareMonitor'ün okuyabildiği her şey için filtrelenebilir **canlı sensörler**.
- Tek tıkla hazır şablonlu **PDF rapor**. Ayrıca JSON çıktısı ve forumlara yapıştırmak için metin özeti. Seri numaraları siz açmadıkça gizlenir.
- **8 dil:** English, Türkçe, Deutsch, Español, Français, Português (Brasil), Русский, 简体中文. Yeniden derlemeden yeni dil eklenebilir.
- Açık ve koyu tema (Windows'u takip eder), Windows 11 Fluent tasarım.

## İndirme ve çalıştırma

1. [Releases](../../releases) sayfasından `CHC.exe` dosyasını indirin.
2. Çalıştırın. CHC, [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) gerektirir (eksikse Windows ilk açılışta kurmayı önerir).

CHC, yönetici izni olmadan **sınırlı modda** açılır. SMART disk verileri ve daha fazla sensör için kenar çubuğundaki **Yönetici olarak aç** düğmesine tıklayın.

İşlemci sıcaklığı ve anakart sensörleri (fanlar, voltajlar) için ayrıca güncel donanım izleme araçlarının kullandığı ücretsiz ve imzalı [PawnIO](https://pawnio.eu) sürücüsü gerekir. CHC bu sürücü olmadan da çalışır ve eksik olduğunda sizi bilgilendirir.

> CHC yalnızca bilgi okur. Hiçbir ayarı değiştirmez, hız aşırtma yapmaz ve disklerinize yazmaz.

## Kaynaktan derleme

Gereksinimler: Windows 10/11 x64 ve [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/aliinal0/CHC.git
cd CHC
dotnet run --project src/CHC.App
```

Tek bir `CHC.exe` oluşturmak için:

```bash
dotnet publish src/CHC.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Bir eşik değerini değiştirmek veya yeni bir kontrol eklemek mi istiyorsunuz? Tüm kurallar `src/CHC.Core/Health/HealthAnalyzer.cs` dosyasında, metinler ise aynı anahtarlarla dil dosyalarında bulunur.

## Çeviriler

Tüm metinler `src/CHC.Core/Localization/Languages/<kod>.json` dosyalarındadır. Yeni dil eklemek için:

1. `en.json` dosyasını `<kod>.json` olarak kopyalayın (örneğin `it.json`).
2. Değerleri çevirin; anahtarları ve `{0}` gibi yer tutucuları değiştirmeyin.
3. Pull request açın ya da dosyayı `CHC.exe` yanındaki `Languages` klasörüne koyun. Yeniden başlattıktan sonra **Ayarlar → Dil** menüsünde görünür.

Ayrıntılar için [CONTRIBUTING.md](CONTRIBUTING.md) dosyasına bakın.

## Lisans

CHC, **[PolyForm Noncommercial License 1.0.0](LICENSE)** ile yayınlanır.

- ✅ CHC'yi kişisel ve ticari olmayan amaçlarla ücretsiz kullanabilir, inceleyebilir, değiştirebilir ve paylaşabilirsiniz.
- ❌ CHC'yi satamaz, ücret karşılığında dağıtamaz veya ticari amaçla kullanamazsınız.

Üçüncü taraf bileşenler kendi lisanslarını korur: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Sorumluluk reddi

Sağlık puanı, donanımınızın bildirdiği verilere ve yaygın kabul gören kurallara dayanır. Yol gösterici bir göstergedir, garanti değildir. Önemli verilerinizi her zaman yedekleyin.
