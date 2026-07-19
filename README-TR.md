# SphereNet

> **.NET 10 (LTS)** ile yazılmış, modern ve yüksek performanslı bir **Ultima Online özel sunucu emülatörü**. [Source-X](https://github.com/Sphereserver/Source-X) ile script uyumluluğu hedeflenir; performans, kalıcılık ve işletilebilirlik açısından onun çok ötesine geçer.

🇬🇧 **Read in English → [README.md](README.md)**
📚 **Dokümantasyon → [docs/](docs/README.md)** (mimari, [staff komutları](docs/STAFF_COMMANDS.md), [trigger'lar](docs/TRIGGERS.md), deploy, runbook) — *İngilizce*
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
| **Dünya** | Housing (client-çizimli multiler, tabela gump'ı, decay, co-owner/friend, lockdown/secure), gemiler (konuşma komutları, akıcı hareket, kalas/ambar, dümenciden dry-dock), partiler, loncalar (savaş/ittifak), hava & gece/gündüz, bitki büyümesi, bölgeler |
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

Aşağıdaki tüm değerler **2026-07-20'de güncel build üzerinde** — erken bir prototip değil, tam özellik setiyle — kod içi araçla ölçüldü: `STRESS` popülasyonu üretir, `BOT` tam UO login akışını tamamlayıp oynayan gerçek TCP istemcileri başlatır. Dünya, tam üretim script paketi (32.842 itemdef, 821 chardef, tüm spawn/bölgeler) ve gerçek MUL haritalarıyla çalışır.

**Test ortamı (bilinçli olarak mütevazı):** 5 vCPU'lu bir sanal makine (AMD Ryzen 9 9950X host), 12 GB RAM, Windows Server 2019, .NET 10 Release, adaptif (DATAS) GC. Botlar **aynı process'te** çalışır, yani istemci tarafı CPU'su da sunucuya yazılır — aşağıdaki her sayı kötümserdir. Tick aralığı **100 ms'dir (saniyede 10 tick, Source-X `MSECS_PER_TICK` paritesi)**; *bütçe*, ortalama bir tick'in bu 100 ms'nin ne kadarını harcadığıdır. Her örnek, spawn/login oturduktan sonraki 30 saniyelik kararlı bir penceredir (300 tick).

### Ölçülen senaryolar

| Senaryo | Ort. | p50 | p95 | p99 | Bütçe | RSS | Değerlendirme |
|---|---|---|---|---|---|---|---|
| 30.000 NPC, hiç oyuncu yok | 0.1 ms | 0.1 ms | <2 ms | <5 ms | <%1 | ~440 MB | ✅ bedava (sektör uykusu) |
| 30.000 NPC + her şehirde yürüyen 300 oyuncu | ~8 ms | ~6 ms | ~13 ms | ~29 ms | %8 | ~580 MB | ✅ rahat |
| 2.000 düşman canavar + savaşan 300 oyuncu | ~3.5 ms | ~2.4 ms | ~7 ms | ~16 ms | %4 | ~470 MB | ✅ rahat |
| Gezinen 1.000 canlı istemci | ~2.5 ms | ~1.9 ms | ~5 ms | ~14 ms | %3 | ~580 MB | ✅ rahat |
| 1.000 istemci + saldıran 2.000 düşman | ~15 ms | ~10 ms | ~30 ms | ~39 ms | %15 | ~690 MB | ✅ pay var |
| 100.000 item + 50.000 NPC + 300 oyuncu — hepsi AKTİF sektörlere yığılmış | ~31 ms | ~24 ms | ~58 ms | ~90 ms | %31 | ~700 MB | ⚠ ağır ama stabil |

Tabloyu okurken:

- **Sektör uykusu tasarlandığı gibi çalışıyor.** Kimse yokken 30.000 NPC tick başına 0.1 ms — büyük ama boş bir dünya fiilen bedava; yalnızca oyuncuların gerçekten gezdiği sektörler için ödeme yapılır.
- **Baskın maliyet aynı anda aktif olan AI'dır**, istemci sayısı değil: gezinen 1.000 istemci ~2.5 ms iken, 2.000 saldırgan canavar eklemek bunu altıya katlar. Oyuncuların göremediği popülasyon hiçbir şeye mal olmaz.
- **Son satır bilinçli bir en-kötü durumdur** — stres üreteci 100 bin item ve 50 bin NPC'nin tamamını oyuncuların bulunduğu şehir sektörlerine bırakır, hiçbir şey uyuyamaz. Buna rağmen döngü 10 Hz tick'ini ortalamada ~3 kat payla korur.
- **GC hiçbir senaryoda hikâyenin konusu olmadı**: bloklayan Gen2 toplamaları her senaryoda 30 sn'lik pencere başına 0–1'de kaldı (duraklama %1–5) — havuzlanan A* scratch'i, tahsissiz yürünebilirlik ve `ArrayPool`'dan kiralanan paket buffer'ları sayesinde.

### Login, açılış, kayıt

| İşlem | Sonuç |
|---|---|
| 300 istemci girişi (tam UO login akışı) | 7.4 sn, 0 hata |
| 1.000 istemci girişi | 18.1 sn, 0 hata, hepsi bağlı kaldı |
| Soğuk açılıştan bağlantı kabulüne (tanımlar + boş dünya) | < 2 sn (tanımlar ~0.2 sn'de yüklenir) |
| Dünya kaydı, 102.400 item + 50.440 karakter (BinaryGz, 3 shard, senkron) | **1.08 sn** — 300 istemci ve 50 bin NPC oynamaya devam ederken ölçüldü |
| Dünya kaydı, ~32.000 nesne | 0.75 sn |

Kayıt anlık görüntüsünün yakalanması çekirdeklere paralelleştirilmiştir; `SAVEBACKGROUND=1` ile shard yazımı düşük öncelikli bir arka plan thread'ine taşınır ve ana döngü yalnızca yakalama maliyetini öder.

### Yoğun tek-ekran kalabalığı — broadcast duvarı

**Tek ekrana** sıkışmış ve hepsi broadcast yapan bir kalabalık en kötü durumdur: konuşma görüş alanındaki her bota yayılır, giden paketler ~N² ölçeklenir. 1.000 bot tek şehir merkezinde (cluster spawn), hepsi konuşurken (ayrı bir broadcast-sel run'ından):

| Metrik | Per-recipient build (eski) | Shared broadcast (mevcut) |
|---|---|---|
| Send-queue overflow | ~919 | **0** |
| Sel altında filo | 1.000 → ~31 | **1.000'in tamamı tam tick hızında tutuldu** |
| Ort. tick (oturmuş) | çöküş | **~3 ms** |
| Bloklayan Gen2 | var | **~0** |
| Çökme / wire bozulması | yok | yok |

Başlangıçta bu per-client send queue'larını dolduruyordu — broadcast paketi alıcı başına yeniden build + yeniden Huffman-compress ediliyordu, özdeş byte'lar N kez yeniden hesaplanıyordu — ve sunucu overflow disconnect'leriyle yük atıyordu (1.000 → ~31). **Tek paylaşılan** paket yayınlamak (bir kez build + compress, tüm alıcılarda tekrar kullan) bu darboğazı kaldırdı: aynı senaryo artık **sıfır** send-queue overflow logluyor ve 1.000 broadcast eden client'ı tam tick hızında tutuyor. Hareket bu duvara aynı şekilde çarpmaz: yoğun kalabalık hareket edemez (mobile'lar birbirini bloklar, adımlar reddedilir, hareket-broadcast fırtınası kendini sınırlar).

Zarif-yavaşlama katmanı olarak **interest management**, geride kalan herhangi bir bağlantıya — ister paket kuyruğu ister gönderilmemiş-byte backlog'u soft cap'i aşsın — giden düşük-öncelikli kozmetik broadcast'leri (overhead speech, ses) düşürür; durum taşıyan paketler (hareket, status, savaş) asla düşürülmez. Normal oyunda atıldır (300-oyuncu combat run'ı hiçbir şey düşürmez) ve yalnızca gerçek bir per-connection backlog'da devreye girer.

**Non-blocking send'ler** yavaş bir client'ın sunucuyu asla durdurmamasını sağlar. Oyun stream'i, non-blocking send'lerle drenaj edilen per-connection kalıcı bir send buffer kullanır — OS send buffer'ı dolunca byte'lar flush thread'ini bloklamak yerine bir sonraki flush'ı bekler. Yani yavaş veya uzak bir client yalnızca kendi buffer'ına mal olur, paylaşılan bir sunucu thread'ine asla.

İkisi birlikte: en kötü durum artık çökmüyor. Chatter geride-kalan bağlantılardan shed edilince, 1.000-bot tek-ekran seli **1.000 client'in tamamını tam tick hızında (~3 ms ort.)** sıfır overflow ve sıfır zorla-disconnect ile tutuyor — server her client'a yalnızca boşaltabileceği kadarını (hareket ve durum) besliyor, kozmetik seli client'ı kesmek yerine per-connection kısıyor.

Daha da aşırı bir sel altında kalan sınır sunucunun *downstream*'indedir: saniyede ~1.000 konuşma paketi alan bir client bunları boşaltamaz (TCP backpressure) — bu "1.000 kişi tek ekranda konuşuyor"un doğasıdır, sunucu darboğazı değil. (Not: bu son sınır, botların sunucuyla CPU paylaştığı in-process harness ile tam ölçülemez.)

### Tahsis & GC

Tick süresindeki dalgalanmanın ana kaynağı bloklayan Gen2 toplamalarıdır; bu
yüzden hot-path'ler tick-başına tahsisten kaçınır. A* pathfinder, her
`FindPath`'te bir PriorityQueue, HashSet ve iki Dictionary tahsis etmek yerine
scratch koleksiyonlarını işçi-thread başına havuzlar (`[ThreadStatic]`, her
çağrıda temizlenir); giden paket buffer'ları ise `ArrayPool<byte>`'dan kiralanıp
socket gönderimi byte'ları tükettikten sonra geri verilir. `[tick_stats]`
yanında her 30 sn'de bir `[gc_stats]` satırı (tahsis oranı, Gen0/1/2 deltaları,
GC pause %'si, heap) yazılır; böylece tahsis regresyonları her run'da görünür.

---

## Hızlı başlangıç

### Ön koşullar

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
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

Paket (~1.900 test); ifade/script motoru, savaş formülleri, kalıcılık/kayıt formatları, paket/era uyumluluğu, hareket, housing/gemiler, hedefleme ve runtime güvenliğini kapsar — her commit'te yeşil tutulur.

---

## Yol haritası & fikirler

Üzerinde aktif olarak düşünülen alanlar (katkılar memnuniyetle):

- Daha geniş büyü-okulu desteği (Chivalry / Bushido / Ninjitsu / Spellweaving / Mysticism)
- Daha zengin NPC AI (sürü taktikleri, kaçış, formasyon hareketi)
- Genişletilmiş web panel (canlı harita görünümü, nesne inceleyici, script konsolu)
- Ek kalıcılık backend'leri ve artımlı kayıt

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
