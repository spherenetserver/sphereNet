# SphereNet

**.NET 10 üzerinde bir Ultima Online sunucu emülatörü.**
[Source-X](https://github.com/Sphereserver/Source-X) scriptleri ve eski Sphere
kayıtlarıyla uyumluluk hedefler. Windows, Linux ve macOS üzerinde çalışır.

[English](README.md) · [Belgeler](docs/README.md) · [Değişiklikler](CHANGELOG-TR.txt)

## Özellikler

- **Script motoru:** Sphere `.scp` tanımları, ifadeler, trigger'lar ve nesne sorguları.
- **Oyun sistemleri:** savaş, büyü, yetenekler, üretim, evler, gemiler, loncalar ve partiler.
- **NPC ve oyuncu botları:** yaratık ve pet yapay zekâsı, yol bulma, sunucunun yönettiği
  oyuncu karakterleri ve isteğe bağlı yerel LLM konuşmaları.
- **Ağ:** UO giriş ve oyun protokolü, istemci sürümüne göre paket işleme,
  Blowfish/Twofish şifreleme ve Huffman sıkıştırma.
- **Motor:** paralel NPC hazırlığı ve istemci görünümü hesaplama, seri script
  yürütme ve dünya güncellemeleri, sektör uykusu ve yalnızca değişen verileri gönderme.
- **Kayıt:** eski kayıtları içe aktarma, isteğe bağlı GZip ile metin/ikili formatlar,
  paralel kayıt parçaları, arka plan kaydı ve MySQL/PostgreSQL/SQLite bağlantıları.
- **Yönetim:** web paneli, yönetim konsolu, yük testleri ve durum kaydı/oynatma.

## Hızlı başlangıç

**.NET 10 SDK**, UO istemci verileri ve bir Sphere/Source-X script paketi gerekir.

```sh
git clone https://github.com/spherenetserver/sphereNet.git
cd sphereNet
```

Başlatmadan önce `config/sphere.ini` dosyasını düzenleyin:

| Ayar | Dizin |
|---|---|
| `MULFILES` | UO istemci verileri |
| `SCPFILES` | Script paketi |
| `WORLDSAVE` | Dünya kayıtları |

```sh
dotnet build src/SphereNet.Server/SphereNet.Server.csproj -c Debug
dotnet run --project src/SphereNet.Server
```

Varsayılan oyun portu **2593**'tür. Web paneli ve sunucuyu panelden başlatmak için
**SphereNet.Host** kullanılır; varsayılan panel portu **9999**'dur.
Kurulum ayrıntıları: [dağıtım](docs/DEPLOY.md) ve [yapılandırma](docs/CONFIG-TR.md).

## Son kayıtlı performans ölçümü

**2026-11-11**, `SphereNet.LoadTest` ile ölçüldü: **2.000 NPC ve 4.000 yer eşyası**
arasında yürüyen, konuşan ve eşya kullanan gerçek TCP istemcileri. Her koşunun
45 saniyelik ölçüm aralığında bir dünya kaydı alınır. Sunucu ve istemciler aynı
AMD Ryzen 7 5700X3D / Windows 11 bilgisayarında çalışır; sunucu 8 işçi kullanır.

| İstemci | Yerleşim | Tick p50 / p95 / p99 | Adım cevabı p50 / p95 / p99 | Bellek ayırma hızı |
|---|---|---|---|---|
| 1.000 | Kasabaya dağılmış | 5,8 / 9,7 / 17,1 ms | 7,4 / 12,7 / 17,9 ms | 50 MB/sn |
| 500 | Tek ekran | 2,9 / 15,2 / 25,0 ms | 5,1 / 12,0 / 42,9 ms | 39 MB/sn |
| 1.000 | Tek ekran | 5,5 / 25,2 / 38,4 ms | 13,0 / 31,3 / 57,4 ms | 79 MB/sn |

Adım cevabı istemci tarafında ölçülür. Tek ekran koşularında
`MaxSizeClientOut=400000` kullanılmıştır; sonuçlar görüş alanına, scriptlere ve
donanıma bağlıdır. Kendi sunucunuzu ölçmek için [performans rehberine](docs/PERFORMANCE.md) bakın.

## Geliştirme ve belgeler

Otomatik testleri çalıştırmak için:

```sh
dotnet test src/SphereNet.Tests/SphereNet.Tests.csproj
```

- [Mimari](docs/ARCHITECTURE.md)
- [Staff komutları](docs/STAFF_COMMANDS.md) ve [script trigger'ları](docs/TRIGGERS.md)
- [Oyuncu botları](docs/PLAYERBOTS.md)
- [İşletim rehberi](docs/RUNBOOK.md)

Hata bildirimleri ve testlerle desteklenen, odaklı PR'ler kabul edilir.
Yapılan değişiklikler [changelog](CHANGELOG-TR.txt) dosyasında tutulur.

**Referanslar:** [Source-X](https://github.com/Sphereserver/Source-X),
[ServUO](https://github.com/ServUO/ServUO), [Ultima Online](https://uo.com/).
