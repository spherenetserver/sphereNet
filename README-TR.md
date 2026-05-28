# SphereNet

> **.NET 9** ile yazılmış, modern ve yüksek performanslı bir **Ultima Online özel sunucu emülatörü**. [Source-X](https://github.com/Sphereserver/Source-X) ile script uyumluluğu hedeflenir; performans, kalıcılık ve işletilebilirlik açısından onun çok ötesine geçer.

🇬🇧 **Read in English → [README.md](README.md)**
📜 **Changelog → [CHANGELOG-TR.txt](CHANGELOG-TR.txt) · [CHANGELOG-EN.txt](CHANGELOG-EN.txt)**

---

## SphereNet nedir?

SphereNet, klasik Sphere tarzı bir UO sunucu çekirdeğinin modern .NET üzerinde sıfırdan yeniden yazılmış halidir. Sunucu yöneticilerinin zaten bildiği şeyleri — `.scp` script dili, trigger modeli, veri formatları — korur; altındaki motoru ise çok çekirdekli donanım, büyük dünyalar ve canlı işletme için yeniden inşa eder.

Elinizde Sphere/Source-X scriptleri ve save verileri varsa, SphereNet bunları minimum değişiklikle çalıştırmayı hedefler; ardından büyümeniz için alan verir: daha çok oyuncu, daha büyük haritalar, daha hızlı kayıt ve her şeyi izleyebileceğiniz canlı bir web paneli.

### Tasarım hedefleri

- **Önce script uyumluluğu** — `.scp` ayrıştırıcı, ifade motoru, trigger'lar ve nesne modeli Source-X davranışını hedefler; mevcut içerik çalışır.
- **Modern donanımda ölçeklenme** — paralel tick hattı, sektör uykusu ve alan-bazlı değişiklik takibi atıl CPU'yu kullanılabilir güce dönüştürür.
- **Üretimde işletilebilirlik** — çoklu kayıt formatı, çoklu veritabanı, SignalR web paneli, Telnet konsolu ve soruşturma için SQLite kayıt/oynatma motoru.
- **Çapraz platform** — Windows, Linux ve macOS üzerinde headless çalışır.
- **Test edilebilir** — ayrıştırıcı, savaş, kalıcılık ve protokol davranışını koruyan büyüyen bir otomatik test paketi.

---

## Özelliklere genel bakış

| Alan | Öne çıkanlar |
|---|---|
| **Scripting** | Source-X `.scp` ayrıştırıcı, tam ifade motoru (matematik, string, nesne sorguları), `FOR`/`WHILE`/`DORAND`/`DOSWITCH` akışı, brace-range `{n m}`, `@`/`MAX`/`MIN`/`QVAL` |
| **Trigger'lar** | `@Login`, `@Death`, `@Hit`, `@GetHit`, `@Click`, `@DClick`, `@Step`, `@Equip`, `@ReceiveItem`, `@Criminal`, `@SeeCrime`, `@SpellInterrupt`, `@Hunger` ve daha fazlası |
| **Savaş** | Era seçilebilir vuruş/hasar formülleri (0/1/2), elemental hasar & direnç, silah/kalkan parry, swing timer, reactive armor, zehir |
| **Büyü** | 60+ büyü, fizzle/kesinti, reagent & mana maliyeti, alan büyüleri, summon, seyahat (Recall/Gate/Mark), buff/debuff süre yönetimi |
| **Yetenekler** | 30+ skill handler ve gain eğrileri, crafting (recipe, exceptional kalite, malzeme hue'su), toplama (madencilik/balıkçılık/oduncu), taming & pet sadakati |
| **NPC AI** | Monster / Pet / Healer / Guard / Vendor / Animal brain'leri, A\* pathfinding, ev-leash, aggro yönetimi |
| **Dünya** | Housing (multi.mul, decay, co-owner/friend, lockdown/secure), partiler, loncalar (savaş/ittifak), hava & gece/gündüz, bölgeler |
| **Adalet** | Criminal flag, murder count, karma/fame, süreli jail, notoriety |
| **Ağ** | Tam UO login akışı, T2A → TOL eklenti paketleri, Blowfish/Twofish/Huffman şifreleme |
| **Kalıcılık** | 4 kayıt formatı, shard tabanlı paralel kayıt, MySQL çoklu veritabanı |
| **İşletim** | SignalR web dashboard, Telnet yönetim konsolu, bot stres testi, SQLite kayıt/oynatma |

---

## Source-X'in Ötesinde

SphereNet'in klasik motorun üzerine eklediği yetenekler.

### 1. Çalışma anında değiştirilebilir çoklu kayıt formatı

Source-X yalnızca düz metin `.scp` kaydeder. SphereNet dört formatı destekler ve aralarında canlı geçiş yapabilir.

| Format | Uzantı | Göreli boyut | Notlar |
|---|---|---|---|
| `Text` | `.scp` | %100 | Source-X uyumlu, insan-okunabilir |
| `TextGz` | `.scp.gz` | ~%15 | Aynı metin, GZip sarılı |
| `Binary` | `.sbin` | ~%50 | Tag-stream binary |
| `BinaryGz` | `.sbin.gz` | ~%8–10 | En küçük ve en hızlı |

**Shard:** `SAVESHARDS=0` tek dosya, `1` boyut-bazlı rolling, `2–16` paralel hash shard (`UID % N`) ile eşzamanlı I/O sağlar. `.SAVEFORMAT BinaryGz 4` komutu format ve shard sayısını tek adımda değiştirir; tek seferlik migration yapar.

### 2. Çoklu veritabanı desteği

Source-X tek bir MySQL bağlantısı destekler. SphereNet aynı anda birden çok veritabanına bağlanır — her birinin kendi host'u, thread modu ve timeout'ları vardır.

```ini
[MYSQL default]
Host=localhost
User=root
Password=secret
Database=sphere
AutoConnect=1

[MYSQL logging]
Host=10.0.0.2
User=logger
Password=logpass
Database=logs
UseThread=1
```

Scriptler aktif bağlantıyı `db.select <isim>` ile değiştirir:

```
db.select logging
db.execute "INSERT INTO logs (msg) VALUES ('event')"
db.select default
db.query "SELECT * FROM users WHERE id=1"
```

### 3. Multicore tick hattı

Source-X tek thread'de çalışır. SphereNet her tick'i dört faza böler, paralelleştirilebilenleri çekirdeklere dağıtır ve herhangi bir hatada **otomatik olarak tek thread'e düşer**.

| Faz | Tür | İş |
|---|---|---|
| Snapshot | Paralel | Sektör tick, NPC snapshot |
| Build | Paralel | NPC karar hesabı (salt-okunur) |
| Apply | Seri | Kararlar UID sırasında uygulanır (deterministik) |
| Flush | Seri | Decay, ışık, telnet, web |

**Region cache:** `FindRegion` her tick'te binlerce kez çağrılır (guard zone, PvP, müzik, hava). Source-X her çağrıda tüm region listesini tarar (O(n)); SphereNet 8×8 tile grid bazlı bir `ConcurrentDictionary` cache kullanır — tekrar tarama yapmaz, region değiştiğinde otomatik temizlenir.

### 4. Sektör uykusu

Source-X her tick'te tüm sektörleri tarar. SphereNet sadece online oyuncu içeren sektörleri tick'ler (her oyuncu etrafında 5×5 sektör penceresi); boş bölgelerdeki NPC ve item'ler sıfır CPU maliyetlidir. 300 oyuncu bir şehre toplandığında haritanın geri kalanı uyur.

**Timer bütünlüğü:** tüm timer'lar (item decay, spawn süresi, `TIMER` trigger) tick sayacı değil mutlak zaman damgası (`Environment.TickCount64`) kullanır. Uyuyan sektörlere 3 dakikada bir yalnızca item timer'larını işleyen hafif bir bakım turu uygulanır — böylece spawner'lar üretmeye devam eder, süresi dolan item'ler silinir ve `TIMER` trigger'ları zamanında ateşlenir; timer kaybı veya kayması olmaz.

### 5. Delta view (alan-bazlı değişiklik takibi)

Source-X her tick'te görünür nesneleri tamamen yeniden gönderir. SphereNet her nesnede alan-bazlı `DirtyFlag` bitmask tutar (Position, Body, Hue, Stats, Equip, …) ve yalnızca değişeni gönderir. View hesabı paralel fazda, paket I/O'su seri fazda çalışır — multicore tick'e uyumlu.

### 6. Bellek-eşlemeli haritalar

Source-X harita dosyalarını tamamen RAM'e yükler. SphereNet `MemoryMappedFile` kullanır; işletim sistemi sayfa yerleşimini yönetir, ~200 MB tasarruf sağlanır.

### 7. NPC timer çarkı

Her NPC'yi her tick'te taramak yerine SphereNet 256 slotlu hash'lenmiş bir timer çarkı kullanır; NPC'ler `nextActionTime`'a göre slot'lanır, O(1) zamanlama.

### 8. Web panel (SignalR canlı dashboard)

ASP.NET Core + SignalR üzerine kurulu gerçek zamanlı yönetim paneli: canlı log akışı, CPU/RAM/thread metrikleri, oyuncu listesi ve sunucu kontrol komutları — hepsi tarayıcıdan. Token-bazlı kimlik doğrulama ve yanıt sıkıştırma içerir. `SphereNet.Host` launcher üzerinden veya standalone çalışır.

### 9. Bot stres-testi sistemi

Dahili bot çerçevesi TCP üzerinden gerçek istemci bağlantılarını simüle eder. Botlar tam UO login akışını (login server → relay → game server) takip eder ve oyun-içi aksiyonlar yapar — yürüme, savaş, loot, skill kullanımı — böylece gerçek oyuncular gelmeden yük testi yapabilirsiniz.

```
.bot spawn 100          # 100 bot oluştur
.bot spawn britain 50   # Britain'de 50 bot
.bot status             # durum göster
.bot stop               # tüm botları durdur
```

### 10. Durum kaydı & oynatma

Source-X'te geçmiş olayları yeniden izleme yoktur. SphereNet, karakter hareketlerini ve durum snapshot'larını belirli aralıklarla yakalayan SQLite tabanlı bir kayıt/oynatma motoru içerir; GM soruşturması, hile tespiti ve debug için geçmişe dönük oynatma sağlar.

---

## Performans

100 ms tick aralığında (saniyede 10 tick) stres testi sonuçları.

**Ortam:** ~50.000 NPC + ~101.000 item + 300 bot (canlı TCP bağlantısı, hepsi tek lokasyonda — en kötü senaryo).

| Ölçüm | Ort. tick | Maks. tick | pps in | pps out | Bütçe |
|---|---|---|---|---|---|
| Başlangıç | 8.7 ms | 35.1 ms | 2.370/s | 790/s | %8.7 |
| Kararlı | 8.9 ms | 33.5 ms | 4.141/s | 802/s | %8.9 |
| Doruk | 9.1 ms | 37.6 ms | 7.366/s | 846/s | %9.1 |

**Kayıt:** 102.780 item + 50.363 karakter → **0.6 sn** (BinaryGz, 3 shard).

**Tick dağılımı** (300 bot, tipik yavaş tick):

| Faz | Ortalama |
|---|---|
| Snapshot | 1.6 ms |
| NPC Build | 1.1 ms |
| NPC Apply | 20.8 ms |
| View Build | 0.7 ms |
| Apply + Flush | 0.4 ms |

`npc_apply` fazı bu yoğunlukta ana darboğazdır; oyuncuların haritaya yayıldığı gerçek dağıtımlarda tick süreleri çok daha düşüktür.

**Karşılaştırma** (300 bot, aynı lokasyon):

| Emülatör | Ort. tick | Maks. tick |
|---|---|---|
| Sphere 56x | 50–80 ms | 150+ ms |
| **SphereNet** | **9.0 ms** | **37.6 ms** |

---

## Hızlı başlangıç

### Ön koşullar

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Ultima Online istemci veri dosyaları (MUL/UOP)

### Derleme & çalıştırma

```bash
git clone https://github.com/Yunusolcay/sphereNet.git
cd sphereNet
dotnet build
```

1. `config/sphere.ini` dosyasını düzenleyin.
2. `MULFILES` değerini UO istemci veri dosyalarınıza yönlendirin.
3. Scriptlerinizi `scripts/` altına koyun.

```bash
dotnet run --project src/SphereNet.Server   # headless konsol
dotnet run --project src/SphereNet.Host     # web panel + sunucu yöneticisi
```

### Varsayılan portlar

| Port | Amaç |
|---|---|
| 2593 | UO istemci |
| 2594 | Telnet yönetim konsolu |
| 2595 | HTTP durum uç noktası |
| 2596 | Web panel (Host modu) |

---

## Yapılandırma

Temel ayarlar `config/sphere.ini` içindedir. En önemli anahtarlardan birkaçı:

| Anahtar | Amaç |
|---|---|
| `MULFILES` | UO istemci verisi yolu (harita, statik, tiledata) |
| `SCPFILES` | Script klasörü kökü |
| `SAVEFORMAT` | `Text` / `TextGz` / `Binary` / `BinaryGz` |
| `SAVESHARDS` | `0` tek dosya, `1` rolling, `2–16` paralel hash shard |
| `[MYSQL <isim>]` | İsimlendirilmiş veritabanı bağlantıları (çoklu-DB) |

Telnet konsolu (port 2594) ve web paneli çalışma anında yönetim sunar; `sphere.ini` değerlerinin çoğu nokta-komutlarıyla canlı değiştirilebilir.

---

## Yönetim

SphereNet GM/yönetici komutlarını konuşma, Telnet konsolu ve web paneli üzerinden sunar. Küçük bir örnek:

```
.SAVE                       # dünyayı şimdi kaydet
.SAVEFORMAT BinaryGz 4      # kayıt formatı + shard değiştir (canlı migration)
.bot spawn 100              # stres-testi botları oluştur
.JAIL <serial> <dakika>     # oyuncuyu süreli hapse at (otomatik salınım)
.GO <x>,<y>,<z>             # ışınlan
.ADD <id|defname>           # item/NPC oluştur
```

Süreli jail cezaları zamanında otomatik sona erer ve yeniden başlatmaya dayanıklıdır (gerçek-saat salınım zamanı olarak saklanır).

---

## Scripting

SphereNet, Sphere tarzı `.scp` içeriğini çalıştırır. Motor şunları destekler:

- **İfadeler:** tam sayı & ondalık matematik, bitwise işlemler, `@` üs operatörü, `MAX`/`MIN`, `ABS`, `SQRT`, trigonometri, `RAND`, brace-range `{n m}` ve ağırlıklı `{a w b w}` seçim.
- **String'ler:** `STRSUB`, `STRLEN`, `STRMATCH`, `STRREGEX`, `STRARG`, `STREAT` ve dahası.
- **Nesne sorguları:** `ISNEARTYPE`, `FINDID`, `FINDLAYER`, `DISTANCE`, `TOPOBJ`, konteyner/karakter iterasyonu (`FORCHARS`, `FORITEMS`, `FORCONT`, …).
- **Akış kontrolü:** `IF`/`ELIF`/`ELSE`, `WHILE`, `FOR` (çoklu argüman formu), `DORAND`/`DOSWITCH`, `BEGIN`/`END`.
- **Nesne modeli:** karakter ve item üzerinde property okuma/yazma ve verb çalıştırma (`STR`, `HITS`, `TAG.*`, `MORE1`, `CONT`, `DUPE`, `ATTACK`, `CURE`, …).

Tanımlar (`CHARDEF`, `ITEMDEF`, `[SPELL ...]`, bölgeler, spawn'lar, template'ler) script klasörünüzden yüklenir ve defname ile çözümlenir.

---

## Proje yapısı

```
src/
├── SphereNet.Core/          # Temel tipler, enum'lar, yapılandırma
├── SphereNet.Network/       # UO protokol, TCP, şifreleme (Blowfish/Twofish/Huffman)
├── SphereNet.Scripting/     # Script ayrıştırıcı, ifade motoru, yürütme
├── SphereNet.Game/          # Oyun mantığı (AI, Combat, Magic, Skills, Death, World, ...)
├── SphereNet.MapData/       # MUL/UOP harita & tiledata okuyucu
├── SphereNet.Persistence/   # Save/load, import
├── SphereNet.Panel/         # SignalR web panel (ASP.NET Core)
├── SphereNet.Host/          # Launcher / sunucu yöneticisi
├── SphereNet.Server/        # Headless sunucu giriş noktası
└── SphereNet.Tests/         # Otomatik test paketi
```

---

## Test

```bash
dotnet test
```

Paket; ifade/script motoru, savaş formülleri, kalıcılık/kayıt formatları, paket/era uyumluluğu, hareket, housing ekonomisi ve runtime güvenliğini kapsar.

---

## Yol haritası & fikirler

Üzerinde aktif olarak düşünülen alanlar (katkılar memnuniyetle):

- Daha geniş büyü-okulu desteği (Necromancy / Chivalry / Bushido / Ninjitsu / Mysticism)
- Bitki büyüme döngüsü (tohum → filiz → ekin → meyve) tam crop durumu ile
- Işık kaynağı tükenme timer'ı (charge takip ediliyor; otomatik sönme bekliyor)
- Daha zengin NPC AI (sürü taktikleri, kaçış, formasyon hareketi)
- Genişletilmiş web panel (canlı harita görünümü, nesne inceleyici, script konsolu)
- Ek kalıcılık backend'leri ve artımlı/asenkron kayıt

---

## Katkı

Issue ve pull request'ler memnuniyetle karşılanır. Lütfen:

- Değişiklikleri odaklı tutun ve mümkün olduğunda testle kapsayın.
- Mevcut kod stiline ve engine-seviyesi adlandırmaya uyun (tanımlayıcılarda üçüncü-taraf ürün adı kullanmayın).
- Göndermeden önce `dotnet build` ve `dotnet test` çalıştırın.

---

## Teşekkürler

- [SphereServer Source-X](https://github.com/Sphereserver/Source-X) — bu projenin uyumluluk için hedeflediği scripting/davranış referansı
- [ServUO](https://github.com/ServUO/ServUO) — çeşitli UO mekanikleri için referans
- [Ultima Online](https://uo.com/) — Origin Systems / Electronic Arts

---

## Lisans

Açık kaynak — [LICENSE](LICENSE) dosyasına bakın.
