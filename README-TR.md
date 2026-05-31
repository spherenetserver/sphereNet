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

Tick döngüsü **50 ms aralıkta (saniyede 20 tick)** çalışır; aşağıdaki *bütçe* sütunu bir tick'in bu 50 ms'nin ne kadarını harcadığıdır. Tüm değerler kod içi araçla ölçüldü — `STRESS` popülasyonu üretir, `BOT` ise giriş yapıp oynayan canlı TCP istemcileri başlatır — gerçek script + MUL kurulumuna karşı, .NET 10 Release ve Server GC ile. Her ölçüm 30 saniyelik bir penceredir (~600 tick).

### Büyük boşta / gezinen dünya — sık karşılaşılan durum

**30.000 NPC + 300 oyuncu yürürken** (canlı TCP botları tüm şehirlerde geziniyor):

| Ölçüm | Ort. | p50 | p95 | p99 | Maks. | Bütçe (ort.) |
|---|---|---|---|---|---|---|
| Başlangıç (üretim + 300 giriş) | 7.3 ms | 2.2 ms | 41.8 ms | 81 ms | 146 ms | %15 |
| Kararlı | ~3.0 ms | 2.2 ms | ~7 ms | ~9 ms | ~42 ms | %6 |

Kararlı durum tam 20 Hz'i korur; tek istisna ara sıra görülen ~40 ms'lik GC duraklamalarıdır (yine bütçe içinde). Sektör uykusu sayesinde oyunculardan uzaktaki NPC'ler sıfır maliyetlidir, böylece büyük bir dünya neredeyse bedavadır.

### Aktif savaş — pahalı durum

**2.000 düşman canavar + 300 oyuncu çarpışırken** (`STRESS 0 2000 mob` + `BOT 300 combat`), tahsis çalışması sonrası yedi adet 30 sn'lik pencerede ölçüldü:

| Ölçüm | Ort. | p50 | p95 | p99 | Gen0/pencere | Gen2/pencere | Tick hızı |
|---|---|---|---|---|---|---|---|
| Erken | 3.6 ms | 0.5 ms | 37 ms | 41 ms | ~50 | ≤2 | 20 Hz |
| Plato (aktif set büyümüş) | ~13 ms | 0.5 ms | 73 ms | 110 ms | ~50 | ~0 | 20 Hz |

Medyan tick ~0.5 ms'de kalır; maliyet, çok sayıda düşmanın aynı anda hedef seçip karşılık verdiği NPC-AI apply fazında yoğunlaşır ve daha fazla canavar saldırıya geçip aktif kaldıkça artar. Bir savaşta baskın maliyet oyuncu sayısı değil, **aynı anda aktif olan savaşçı sayısıdır**. 20 Hz baştan sona korunur.

Tahsis çalışmasından önce (havuzlanan A* scratch + tahsissiz yürünebilirlik + havuzlanan paket buffer'ları) aynı senaryo ~21–29 ms ort., p95 85–119 ms, pencere başına ~230–255 Gen0 ve 1–3 bloklayan Gen2 toplamasıyla çalışıyordu — yani havuzlama tick süresini kabaca yarı-ila-çeyreğe indirdi ve tick jitter'ını süren bloklayan Gen2 duraklamalarını neredeyse tümüyle giderdi.

**Tavan:** 30.000 *düşman* canavar + 300 oyuncu döngüyü doyuma ulaştırır (~600–800 ms tick, ~1–2 Hz) — ancak çökme veya çok çekirdekten tek çekirdeğe düşme olmadan zarif şekilde yavaşlar. On binlerce eşzamanlı savaşan AI tek bir 50 ms karesini aşar; bu büyüklükteki boşta popülasyonlar aşmaz.

### 1.000 eş zamanlı client

1.000 canlı TCP botu (hepsi loopback'ten) 25–30 sn'de sıfır hatayla bağlanır ve tüm run boyunca bağlı kalır — kopma yok, bozuk paket yok.

| Senaryo | Ort. | p50 | p95 | p99 | Tick hızı | pps out |
|---|---|---|---|---|---|---|
| 1.000 yayılmış + düşük aktivite | ~0.8 ms | 0.7 ms | ~1.5 ms | 5–29 ms | 20 Hz | ~1.750/s |
| 1.000 aktif savaşta (+2.000 düşman) | ~32–34 ms | ~1 ms | ~130 ms | ~210–320 ms | ~19 Hz | ~1.950/s |

Bin yayılmış oyuncu neredeyse bedava (Gen0 ~1/pencere, bloklayan Gen2 yok). Bin oyuncu *aynı anda savaşırsa* döngü ~34 ms ort.'ya çıkar — çalışır, 20 Hz büyük ölçüde korunur, ama p95/p99 50 ms karesini aşar; yani bu yoğunlukta pratik sınır budur. Baskın maliyet client sayısı değil aktif-savaşçı sayısıdır: 1.000 yayılmış ≈ bedava, 1.000 hepsi-savaşta ≈ bütçe kenarı.

(Sayılar kötümser: 1.000 bot sunucuyla aynı process'te CPU paylaşır.)

### Yoğun tek-ekran kalabalığı — broadcast duvarı

Asıl ölçekleme sınırı **tek ekrana** sıkışmış ve hepsi broadcast yapan bir kalabalıktır. 1.000 botu tek şehir merkezine yığıp (cluster spawn) konuşturmak — konuşma görüş alanındaki her bota yayılır, yani giden paketler ~N² ölçeklenir — per-client send queue'larını doldurur:

| Metrik | Sonuç |
|---|---|
| Send-queue overflow | ~919 |
| Yük-atma sonrası filo | 1.000 → ~31 hayatta kalan |
| Cull sonrası | 20 Hz, ~0.4 ms ort. (toparlandı) |
| Tepe RSS | ~1.8 GB, sonra düştü |
| Çökme / bozulma | yok — zarif yük atma |

Broadcast oranı serial send yolunun boşaltabileceğini aşınca, sunucu çökmek veya teli bozmak yerine send-queue-overflow disconnect'leriyle yük atar — sınırlı bellek, sonra toparlanma. Bu, tek broadcast eden ekran için pratik tavan ve bilinen ölçekleme duvarıdır. Hareket bu duvara aynı şekilde çarpmaz: yoğun kalabalık hareket edemez (mobile'lar birbirini bloklar, adımların çoğu reddedilir, hareket-broadcast fırtınası kendini sınırlar). Bu tavanı yükseltecek değişiklik: her alıcıya ayrı paket üretmek yerine (mevcut davranış) tüm alıcılara **tek paylaşılan** serileştirilmiş paket yayınlamaktır — doğal bir sonraki optimizasyon.

**Kayıt:** 102.780 item + 50.363 karakter → **0.6 sn** (BinaryGz, 3 shard).
**Bellek:** Yukarıdaki tüm senaryolarda ~550–650 MB çalışma kümesi.

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
