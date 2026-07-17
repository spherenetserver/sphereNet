# SphereNet Performans Logu İncelemesi

## Belge amacı

Bu belge, aşağıdaki canlı sunucu logunda görülen `world save`, `loop_stall` ve
`slow_tick` uyarılarını mevcut SphereNet kaynak koduyla eşleştirir:

```text
[22:59:52 INF] Saving world...
[22:59:52 INF] World save #79 starting (format=Text, shards=3)...
[22:59:52 INF] World save #79 complete: 46207 items, 6374 chars in 0,5s
[22:59:52 INF] Save complete. (0.48 sec)
[22:59:52 WRN] [loop_stall] total=483,5ms ... jobs=483,4ms ... gc0=+6 gc1=+4 ...

[23:00:53 WRN] [slow_tick] ... total=57,7ms dominant=snapshot snapshot=57,6ms ...

[23:07:01 WRN] [loop_stall] total=169,6ms ... net_in=136,8ms ... pkts=0 ...
[23:07:03 WRN] [slow_tick] ... total=261,2ms dominant=snapshot snapshot=257,0ms ...
[23:07:03 WRN] [loop_stall] total=271,1ms ... ticks=261,4ms ...
[23:07:05 WRN] [loop_stall] total=257,7ms ... ticks=257,6ms ...
[23:07:07 WRN] [loop_stall] total=176,4ms ... yield=176,2ms ...
[23:07:09 WRN] [loop_stall] total=211,4ms ... net_in=179,0ms ... pkts=0 ...
[23:07:11 WRN] [loop_stall] total=880,8ms ... ticks=880,1ms ...
[23:07:13 WRN] [loop_stall] total=104,5ms ... net_out=101,1ms ...
...
[23:07:28 WRN] [loop_stall] total=200,5ms ... net_in=198,6ms ... pkts=0 ...
[23:07:28 WRN] [slow_tick] ... total=55,8ms dominant=snapshot snapshot=55,7ms ...
[23:07:30 WRN] [loop_stall] total=234,2ms ... yield=233,4ms ...
[23:07:32 WRN] [loop_stall] total=292,7ms ... yield=279,0ms ...
[23:07:34 WRN] [loop_stall] total=293,7ms ... net_out=282,1ms ...
```

İnceleme sırasında kaynak kod değiştirilmemiştir. Yalnızca bu Markdown raporu
oluşturulmuştur.

---

## Kısa sonuç

Log tek bir performans problemine değil, en az iki ayrı sınıfa işaret ediyor:

1. **Kesin olarak doğrulanan save durması:** Dünya kaydı ana oyun döngüsünde
   senkron çalışıyor. `0,48 saniyelik` save süresi ile `483,5 ms` loop stall
   birebir örtüşüyor. Bu sırada oyuncular yaklaşık yarım saniyelik donma
   yaşayabilir.

2. **23:07 civarındaki yaygın zamanlayıcı/CPU gecikmesi:** Gecikmeler yalnız
   `snapshot` veya NPC kodunda değil; paket işlenmediği halde `net_in`, hiçbir
   oyun işi yokken `yield` ve farklı anlarda `net_out` aşamalarında da görülüyor.
   Özellikle `Thread.Sleep(0)` çağrısının `176–279 ms` sürmüş görünmesi, ana
   thread'in işletim sistemi tarafından zamanında tekrar çalıştırılmadığını
   gösteren güçlü bir belirtidir. Bunun en olası üst seviye nedenleri:

   - Sunucu makinesinde veya sanal makine hostunda CPU doygunluğu,
   - Ana thread'in başka hazır thread/process'ler yüzünden uzun süre sıra
     beklemesi,
   - Çok çekirdekli tick işçilerinin bütün mantıksal çekirdekleri kullanması,
   - Disk/SQLite/antivirüs gibi arka plan işlerinin oluşturduğu sistem baskısı,
   - Ağ bağlantı kabulü veya login aşamasındaki bloklayan soket işlemleri.

3. **Motor içinde gecikmeyi büyütebilecek tam-dünya taramaları var:** Yaklaşık
   `46.207 item + 6.374 character = 52.581` kalıcı objeli dünyada bazı yollar
   bütün objeleri periyodik olarak, bazıları ise her tick tarıyor. Normal bir
   makinede bunların her biri tek başına `880 ms` açıklamayabilir; fakat CPU
   baskısı altında sıçramaları büyütebilir.

Bu logdan “kesin sorun yalnız snapshot kodu” veya “kesin sorun network” sonucu
çıkarılamaz. Duvar saati ölçümü sırasında thread başka bir iş için durdurulursa,
bekleme süresi o anda açık olan faza yazılır. Bu nedenle `net_in=198,6ms` değeri,
gerçek ağ kodunun 198,6 ms CPU kullandığını tek başına kanıtlamaz.

---

## Önem ve öncelik özeti

| Öncelik | Bulgu | Güven | Oyuncu etkisi |
|---|---|---:|---|
| P0 | Save tamamen ana loop üzerinde ve yaklaşık 483 ms durduruyor. | Kesin | Periyodik yarım saniyelik donma |
| P0 | `23:07` kümesinde ana thread zamanlama gecikmesi/host baskısı belirtisi var. | Yüksek | 100–880 ms düzensiz ping ve hareket donması |
| P1 | `snapshot` adı tek bir snapshot işlemini değil geniş bir world-tick grubunu ölçüyor. | Kesin | Yanlış kök neden seçilmesi riski |
| P1 | `TIMERF` yolu her server tick'inde tüm dünya objelerini tarıyor. | Kesin | Dünya büyüdükçe sabit tick maliyeti |
| P1 | StateRecorder çağrısı her tick'te tüm objelerin yeni dizi kopyasını oluşturuyor. | Kesin | Tahsis/GC baskısı ve periyodik tarama |
| P1 | Uyuyan sektör bakımı her 3 dakikada item bulunan tüm sektörleri topluca geziyor. | Kesin | Periyodik `snapshot` sıçraması adayı |
| P1 | Ağ kabul, input ve output ana oyun thread'inde; login/unknown send yolu bloklayabilir. | Kesin altyapı, olayla bağlantısı olası | Bağlantı veya yavaş istemci kaynaklı loop stall |
| P2 | Her 5 saniyede decay için tüm dünya objeleri taranıyor. | Kesin | Periyodik küçük/orta flush maliyeti |
| P2 | Otomatik worker sayısı bütün işlemci çekirdeklerini kullanabilir. | Kesin ayar, olayla bağlantısı olası | Paylaşımlı hostta ana thread starvation |
| P2 | StateRecorder SQLite flush/cleanup arka planda disk ve CPU ile yarışabilir. | Olası | Özellikle yavaş disk üzerinde dalgalanma |
| P3 | GC, `23:07` kümesinin ana açıklaması değildir. | Yüksek | Yanlış optimizasyonu önler |
| P3 | Yavaş bir paket handler'ı gösteren kanıt yoktur. | Yüksek | Paket koduna erken müdahaleyi önler |

---

## Log alanları gerçekte neyi ölçüyor?

### `loop_stall`

Ana loop içindeki bir tam turun duvar saati süresidir. Kaynak:
[Program.Tick.cs](../src/SphereNet.Server/Program.Tick.cs), yaklaşık
`90–254`.

Fazlar:

| Alan | İçerik |
|---|---|
| `cmd` | Konsol komutları ve `_mainLoopActions` kuyruğu |
| `net_in` | Yeni bağlantı kabulü ve tüm istemcilerin input işlemesi |
| `jobs` | Movement queue, dirty-view fast path, stress işleri, bot restock, auto-save, server timer hook ve replay |
| `net_out` | Tüm istemcilerin output flush'ı ve connection timeout/cleanup |
| `ticks` | O turda zamanı gelmiş server tick'leri; bir turda en fazla dört catch-up tick |
| `yield` | `TickSleepMode` tarafından yapılan spin/sleep/yield |
| `gc0/1/2` | Yalnız o loop turunda tamamlanan GC koleksiyon sayısı |
| `pkts` | O input geçişinde kayıtlı packet handler'a ulaşmış paket sayısı |
| `slowest_pkt` | Yalnız packet handler gövdesinde ölçülen en yavaş opcode |

Önemli sınırlama: Bu ölçümler CPU süresi değil, **duvar saati süresidir**.
İşletim sistemi thread'i 200 ms çalıştırmazsa, bekleme hangi faz sırasında
olduysa o faz 200 ms görünür.

### `slow_tick`

Yalnız `RunServerTick()` süresini ölçer. Kaynak:
[Program.Tick.cs](../src/SphereNet.Server/Program.Tick.cs), yaklaşık
`309–445`.

Logdaki `dominant=snapshot` etiketi özellikle dikkatle yorumlanmalıdır.
Multicore modda bu alan yalnız “bir liste snapshot'ı” değildir. Aşağıdakilerin
tamamı aynı süreye dahildir:

- `_world.OnTickParallel(...)`
- Aktif sektörlerin yeniden hesaplanması
- Aktif sektörlerde character ve item `OnTick()`
- Her üç dakikada uyuyan sektör item bakımı
- Bütün dünya üzerindeki `TIMERF` taraması
- Spell expiration
- Yeni aktif sektörlerdeki NPC'lerin uyandırılması
- NPC timer wheel `Advance`
- Aktif client listesinin hazırlanması

Kaynak:
[Program.Tick.cs](../src/SphereNet.Server/Program.Tick.cs), yaklaşık
`607–631`; [GameWorld.cs](../src/SphereNet.Game/World/GameWorld.cs), yaklaşık
`1269–1321`.

Dolayısıyla `snapshot=257ms` şu anlama gelir:

> Yukarıdaki geniş world-tick grubunun toplamı 257 ms sürdü.

Hangi alt işlemin 257 ms sürdüğü mevcut logla bilinmiyor.

### Uyarı loglarının örnekleme sınırı

- `loop_stall` en fazla yaklaşık **iki saniyede bir** yazılıyor.
- `slow_tick` en fazla yaklaşık **on saniyede bir** yazılıyor.

Bu nedenle `23:07:05` ve `23:07:11` tick stall'ları için ayrı `slow_tick`
detayı bulunmaması, bu tick'lerin ölçülmediği anlamına gelmez. Loglama rate-limit
nedeniyle faz detayı bastırılmıştır.

Bu durum ayrıca `23:07:01–23:07:34` arasında görülen yaklaşık iki saniyelik
satır aralığının, problemin iki saniyede bir oluştuğunu kanıtlamadığı anlamına
gelir. Aralık büyük ölçüde logger sınırlamasından kaynaklanır; arada daha fazla
stall yaşanmış olabilir.

---

## Log zaman çizelgesinin değerlendirilmesi

### 22:59:52 — World save

| Ölçüm | Değer |
|---|---:|
| Persist edilen item | 46.207 |
| Persist edilen character | 6.374 |
| Toplam kalıcı obje | 52.581 |
| Save formatı | `Text` |
| Shard sayısı | 3 |
| Save süresi | 0,48 saniye |
| Loop stall | 483,5 ms |
| `jobs` fazı | 483,4 ms |
| GC | Gen0 +6, Gen1 +4, Gen2 +0 |

Bu olayın kök nedeni kesindir. `PerformSave()` ana loop içinde doğrudan çağrılır
ve sırasıyla:

- House/ship/guild verilerini tag'lere yazar,
- Aktif spell dönüşümlerini save için geri alır,
- Bütün world objelerinin snapshot'ını üretir,
- Shard dosyalarını yazar,
- Server data ve account dosyalarını kaydeder,
- Spell dönüşümlerini yeniden uygular.

Kaynaklar:

- [Program.Tick.cs](../src/SphereNet.Server/Program.Tick.cs), yaklaşık `187–196`
- [Program.Persistence.cs](../src/SphereNet.Server/Program.Persistence.cs),
  yaklaşık `213–270`
- [WorldSaver.cs](../src/SphereNet.Persistence/Save/WorldSaver.cs), yaklaşık
  `74–100` ve `206–254`

`WorldSaver` önce `world.GetAllObjects().ToArray()` üzerinden bütün objeleri
toplar, ardından her item/character için bir `SaveRecord` oluşturur. Üç shard'ın
disk yazımı `Task.Run` ile paralel olsa da ana thread `Task.WaitAll` ile bu
işlerin bitmesini bekler. Dolayısıyla shard yazımının paralel olması, oyuncu
thread'ini serbest bırakmaz.

### 23:00:53 — 57,7 ms snapshot

Bu, yapılandırılmış `SlowTickWarnMs=50` eşiğini yalnız 7,7 ms aşan bir olaydır.
Varsayılan `ServerTickMs=100` kabul edilirse tek başına kritik bir tick bütçesi
aşımı değildir. İzlenmelidir ancak `261–880 ms` olaylarıyla aynı ciddiyette
değildir.

### 23:07:01 — `net_in=136,8ms`, `pkts=0`

`pkts=0`, hiçbir kayıtlı packet handler'ın çalışmadığını gösterir. Ancak
`net_in` fazı şunları da içerir:

- Listener `Poll` ve sınırsız accept döngüsü,
- Kabul edilen her bağlantı için IP limit taraması,
- Boş slot bulma,
- Her aktif socket için `Connected`, `Available` ve `Receive`,
- İlk seed/encryption hazırlığı.

Bu nedenle olasılıklar:

1. Thread `net_in` fazı sırasında işletim sistemi tarafından bekletildi.
2. Sunucuya aynı anda çok sayıda bağlantı girişimi geldi.
3. Çok sayıda aktif/yarım login bağlantısı socket taramasını büyüttü.
4. Socket/kernel çağrılarından biri beklenenden uzun döndü.

Çevre loglarında çok sayıda `Connection #... from ...`, IP limit veya
disconnect satırı varsa connection-flood/scanner ihtimali yükselir.

### 23:07:03 — `snapshot=257ms`

Bu olay kesin olarak server tick içindedir. Fakat “snapshot” grubunun genişliği
nedeniyle aşağıdaki alt nedenlerden hangisi olduğu bilinmiyor:

- Üç dakikalık uyuyan sektör item bakımı,
- Her tick çalışan bütün dünya `TIMERF` taraması,
- Yeni ve yoğun bir bölgeye girilmesiyle çok sayıda sektör/NPC uyanması,
- Aktif sektörlerden birinde çok fazla item veya character bulunması,
- Spell expiration,
- Timer wheel işlemi,
- Bu faz sırasında ana thread/worker thread'lerinin işletim sistemi tarafından
  bekletilmesi.

Saatin tam `...:03` olması tek başına üç dakikalık maintenance kanıtı değildir.
Maintenance zamanı sunucu uptime'ına bağlıdır. Ancak bu olayın araştırılmasında
ilk kontrol edilmesi gereken adaylardan biridir.

### 23:07:05 ve 23:07:11 — `ticks=257,6ms` ve `ticks=880,1ms`

Bu iki olay ciddidir. Ayrıntılı `slow_tick` satırı on saniyelik rate-limit
nedeniyle yoktur. Bunların `snapshot`, başka tick fazı veya thread preemption
olduğu mevcut logdan belirlenemez.

`880,1ms`, varsayılan 100 ms tick aralığının yaklaşık dokuz katıdır. Ana loop
bir turda en fazla dört catch-up tick çalıştırabilir. Bu nedenle bu toplam:

- Tek bir aşırı yavaş tick,
- Birden fazla gecikmiş tick'in aynı turda çalışması,
- Ya da her ikisinin birleşimi

olabilir.

### 23:07:07, 23:07:25, 23:07:30, 23:07:32 — `yield`

En güçlü sistem-zamanlama kanıtı bu satırlardır:

```text
yield=176,2ms
yield=106,6ms
yield=233,4ms
yield=279,0ms
```

Aktif ayar `TickSleepMode=2` ise yield yolu:

```text
Thread.SpinWait(100);
Thread.Sleep(0);
```

çalıştırır. Kaynak:
[TickYieldStrategy.cs](../src/SphereNet.Server/TickYieldStrategy.cs).

`Thread.Sleep(0)` süre garantisi vermez. Thread kalan zaman dilimini bırakır ve
yeniden CPU alana kadar bekler. Yüzlerce milisaniyelik dönüş süreleri aşağıdaki
durumlarla uyumludur:

- CPU'nun tamamının kullanılması,
- Aynı veya yüksek öncelikli çok sayıda hazır thread,
- Aşırı yüklenmiş/paylaşımlı sanal makine,
- Host seviyesinde CPU steal/scheduling gecikmesi,
- Güç tasarrufu veya ağır arka plan prosesi,
- Antivirüs, yedekleme veya disk filtre sürücüsü etkisi.

Bu gecikmeyi bir oyun nesnesi, NPC AI veya packet handler doğrudan açıklayamaz;
yield sırasında bu kodların hiçbiri çalışmıyor.

### 23:07:09 ve 23:07:28 — uzun `net_in`, yine `pkts=0`

Yavaş packet handler hipotezi bu iki örnekte desteklenmiyor. `slowest_pkt=0x00`
ve `pkts=0`.

Ancak accept flood veya handler öncesi socket/crypto yolu hâlâ olasıdır.
Diğer yandan uzun `yield` olaylarıyla aynı pencere içinde bulunması, genel
thread scheduling gecikmesini daha güçlü aday yapar.

### 23:07:13 ve 23:07:34 — uzun `net_out`

Game bağlantılarında send non-blocking moda geçiriliyor. Buna karşılık login
ve henüz türü belirlenmemiş bağlantılar farklı send yoluna giriyor ve doğrudan
`Socket.Send` kullanıyor. Socket `SendTimeout=5000` olarak ayarlanıyor.

Kaynak:
[NetState.cs](../src/SphereNet.Network/State/NetState.cs), yaklaşık
`233–265`, `535–703` ve `819–825`.

Bu yüzden `net_out=101–282ms` için iki ana aday vardır:

1. Thread bu faz sırasında CPU'dan alındı.
2. Login/unknown durumundaki bir socket'in bloklayan send yolu bekledi.

Mevcut log output byte sayısını, kuyruk boyutunu, bağlantı türünü veya en yavaş
client'ı yazmadığı için bu ikisi ayrılamıyor.

### 23:07:28 — ayrı bir 55,8 ms `slow_tick`

Aynı saniyedeki `net_in=198,6ms` loop stall ile `snapshot=55,7ms` slow tick
muhtemelen iki farklı loop turuna aittir. Aynı tur olsaydı toplam sürenin en az
yaklaşık 254 ms olması beklenirdi. Log timestamp'i yalnız saniye hassasiyetinde
olduğu için iki ayrı olay aynı saniye altında görünmüş olabilir.

---

## Kaynak kodda tespit edilen performans riskleri

## 1. Save ana thread'i tamamen blokluyor

### Durum

Kesin.

### Kanıt

Auto-save doğrudan ana loop içinde `PerformSave()` çağırıyor. Arka plan save
ayarları config'e yükleniyor fakat kademeli/background save uygulanmamış:

```ini
SAVEBACKGROUND=0
SAVESECTORSPERTICK=1
```

`sphere.ini` açıklaması da bu iki ayarın okunduğunu fakat WorldSaver tarafından
kullanılmadığını belirtiyor.

### Ek uyumsuzluk

Repository içindeki örnek/aktif config:

```ini
SAVEFORMAT=BinaryGz
SAVESHARDS=3
```

fakat canlı log:

```text
format=Text, shards=3
```

gösteriyor. Bu şu ihtimallerden birini gösterir:

- Çalışan sunucu farklı bir `sphere.ini` okuyor,
- Runtime `.SAVEFORMAT Text` komutuyla değiştirilmiş,
- Publish klasöründeki config repository config'inden farklı,
- Başlatma yolu beklenenden farklı.

Aktif runtime config doğrulanmadan yalnız repository `config/sphere.ini`
üzerinde ayar değiştirmek sonuç vermeyebilir.

### Etki

Şimdiki dünya boyutunda yaklaşık 0,48 saniye. Dünya büyüdükçe snapshot,
serialization, allocation ve disk yazımı maliyeti artacaktır.

---

## 2. `snapshot` telemetrisi yanlış anlaşılmaya çok açık

### Durum

Kesin.

### Sorun

`_telemetrySnapshotUs`, tek bir snapshot oluşturma süresinden çok daha geniş bir
iş grubunu ölçüyor. Bu gruptaki pahalı alt işlemler ayrı ayrı loglanmıyor.

### Etki

`snapshot=257ms` görüldüğünde yanlışlıkla yalnız client listesi veya collection
copy koduna odaklanılabilir. Gerçek neden sektör tick'i, timer taraması, sleeping
maintenance veya OS scheduling olabilir.

### Planlanan telemetri ayrımı

İleride aşağıdaki alt sürelerin ayrı ölçülmesi gerekir:

- `active_sector_refresh`
- `active_sector_tick`
- `sleeping_sector_maintenance`
- `timerf_scan`
- `spell_expiration`
- `new_sector_npc_wakeup`
- `npc_wheel_advance`
- `client_snapshot`
- `scheduler_gap`

Bu yalnız ölçüm planıdır; bu incelemede kod değişikliği yapılmamıştır.

---

## 3. `TIMERF` her tick bütün dünya objelerini tarıyor

### Durum

Kesin.

### Kod davranışı

`GameWorld.TickTimerF()` her world tick'te:

```csharp
foreach (var obj in _objects.Values)
```

ile bütün objeleri dolaşıyor; yalnız `TimerFEntries.Count > 0` olanları ikinci
listeye ekliyor.

Kaynak:
[GameWorld.cs](../src/SphereNet.Game/World/GameWorld.cs), yaklaşık `1127–1149`.

### Canlı dünya üzerindeki büyüklük

Save loguna göre her 100 ms'lik server tick'te yaklaşık 52.581 obje kontrol
ediliyor olabilir. Varsayılan 10 tick/s ile bu, kabaca saniyede 525 bin obje
kontrolü demektir.

### Etki

- Aktif TIMERF olmasa bile tarama devam eder.
- Dünya büyüdükçe tick tabanı doğrusal büyür.
- Concurrent collection enumeration ve cache miss maliyeti oluşur.
- CPU baskısı altında `snapshot` sıçramalarını büyütebilir.

### Daha uygun altyapı yönü

TIMERF girdilerinin merkezi bir due-time kuyruğu/min-heap/timer wheel ile
izlenmesi ve yalnız zamanı gelen objelerin işlenmesi gerekir.

---

## 4. StateRecorder her tick bütün objelerin dizi kopyasını oluşturuyor

### Durum

Kesin.

### Kod davranışı

Her multicore ve single-thread tick'te şu çağrı yapılıyor:

```csharp
_stateRecorder?.Tick(
    Environment.TickCount64,
    _world.GetAllObjects().OfType<Character>());
```

`GetAllObjects()` lazy bir collection dönmek yerine içeride doğrudan:

```csharp
_objects.Values.ToArray()
```

çalıştırıyor.

Kaynaklar:

- [Program.Tick.cs](../src/SphereNet.Server/Program.Tick.cs), yaklaşık `566`
  ve `788`
- [GameWorld.cs](../src/SphereNet.Game/World/GameWorld.cs), yaklaşık `1467–1473`

StateRecorder kendi içinde hareket taramasını yalnız iki saniyede bir,
snapshot'ı 15 saniyede bir yapıyor. Fakat çağrı argümanı metoda girilmeden önce
değerlendirildiği için 52.581 elemanlık obje dizi kopyası **her tick** üretiliyor.

### Yaklaşık tahsis etkisi

64-bit process üzerinde yalnız referans dizisi kabaca:

```text
52.581 × 8 byte ≈ 421 KB/tick
```

Varsayılan 10 tick/s için yalnız bu dizi yaklaşık `4 MB/s` managed allocation
oluşturabilir. Dizi header ve diğer LINQ maliyetleri buna dahil değildir.

### Log ile ilişki

`23:07` stall satırlarında GC deltası `+0` olduğundan tek tek bu stall'ların
doğrudan GC pause olduğu söylenemez. Ancak sürekli tahsis, başka loop
turlarındaki GC sıklığını ve genel CPU/cache baskısını artırır.

### StateRecorder arka plan etkisi

StateRecorder:

- Hareketleri iki saniyede bir tarar,
- Snapshot'ları 15 saniyede bir kuyruğa alır,
- SQLite'a ayrı thread'den en geç üç saniyede bir yazar,
- On dakikada bir eski kayıtları silen cleanup işi başlatır.

WAL ve `synchronous=NORMAL` kullanılması olumlu olsa da, save dosyalarıyla aynı
disk üzerinde SQLite flush/cleanup disk rekabeti yaratabilir.

### Daha uygun altyapı yönü

- Önce interval kontrolü, sonra character snapshot oluşturulmalı.
- `GetAllCharactersSnapshot()` veya yalnız online-player listesi kullanılmalı.
- `playersOnly=1` iken tüm NPC'leri dolaşan yol tamamen atlanmalı.
- SQLite flush ve cleanup aynı connection üzerinde açık biçimde
  serileştirilmeli.

---

## 5. Uyuyan sektör item bakımı tek tick'te toplu çalışıyor

### Durum

Kesin.

### Kod davranışı

Her üç dakikada bir:

1. Bütün map sector grid'leri dolaşılır.
2. Aktif olmayan fakat item içeren her sektör bulunur.
3. Bu sektörlerdeki tüm uyanık item'lar `OnTick()` alır.

Kaynak:
[GameWorld.cs](../src/SphereNet.Game/World/GameWorld.cs), yaklaşık
`1223–1243` ve `1311–1317`;
[Sector.cs](../src/SphereNet.Game/World/Sectors/Sector.cs), yaklaşık `358–374`.

`ObjBase` içindeki `_isSleeping` varsayılan olarak `false` olduğundan,
özellikle açıkça `GoSleep()` çağrılmayan item'lar maintenance sırasında
işlenir.

### Etki

46 bin item'lı dünyada çalışma yükü tek bir server tick'e yığılabilir. Bu,
periyodik `snapshot` sıçramalarının güçlü bir adayıdır.

### Daha uygun altyapı yönü

- Sleeping-sector bakımını tick başına zaman/obje bütçesiyle parçalara bölmek,
- Sonraki tick'te devam eden cursor tutmak,
- Timer/decay/spawn due indexleriyle yalnız işi olan item'ı çalıştırmak.

---

## 6. Aktif sektör ve yeni bölgeye giriş maliyeti

### Durum

Kesin altyapı; canlı olayla bağlantısı olası.

### Kod davranışı

Her online oyuncu çevresinde `5×5` sektör aktif kabul ediliyor. Oyuncuların
pencereleri çakışmıyorsa aktif sektör sayısı oyuncu sayısıyla büyüyor. Her aktif
sektörde:

- Uyku durumunda olmayan character'lar `OnTick()` alır,
- Uyku durumunda olmayan ground item'lar `OnTick()` alır.

Oyuncu yeni sektöre girdiğinde yeni aktif olan sektörlerdeki NPC'ler timer
wheel'e topluca eklenir.

Kaynaklar:

- [GameWorld.cs](../src/SphereNet.Game/World/GameWorld.cs), yaklaşık `1151–1210`
- [Sector.cs](../src/SphereNet.Game/World/Sectors/Sector.cs), yaklaşık `340–374`
- [Program.Tick.cs](../src/SphereNet.Server/Program.Tick.cs), yaklaşık `824–837`

### Etki

Şu senaryolar `snapshot` sıçraması yaratabilir:

- Oyuncunun çok yoğun bir vendor/spawn bölgesine girmesi,
- Oyuncuların birbirinden uzak haritalara dağılması,
- Bir sektörde anormal sayıda ground item veya NPC bulunması,
- `SECF_NoSleep` ile sürekli açık bırakılmış çok sayıda sektör.

### Mevcut koruma

NPC kararları tick başına 500 ile bütçeleniyor. Ancak yeni sektör NPC'lerini
uyandırma, aktif sektör character/item tick'i ve timer queue işlemlerinin
tamamı bu 500 karar bütçesinin dışında maliyet üretebilir.

---

## 7. Decay catch-up her beş saniyede bütün objeleri tarıyor

### Durum

Kesin.

### Kod davranışı

`RunDecayCatchup()` beş saniyede bir `CollectExpiredGroundItems()` çağırıyor.
Bu metod, en fazla 256 süresi geçmiş item bulmak için `_objects.Values`
üzerinde baştan sona tarama yapıyor.

Kaynaklar:

- [Program.Tick.cs](../src/SphereNet.Server/Program.Tick.cs), yaklaşık `864–888`
- [GameWorld.cs](../src/SphereNet.Game/World/GameWorld.cs), yaklaşık `1666–1684`

Hiç süresi geçmiş item yoksa bütün dünya gezilir. Bu maliyet `slow_tick`
telemetrisinde `flush` grubuna girer.

### Log ile ilişki

Gösterilen iki ayrıntılı slow tick'te `flush=0,1–4,2ms` olduğu için bu örneklerin
ana sebebi decay taraması değildir. Yine de büyüyen world için tekrarlı bir
tam-dünya taraması olarak izlenmelidir.

---

## 8. Network tamamen ana loop üzerinde

### Durum

Kesin.

### Kod davranışı

Repository config'inde:

```ini
USEASYNCNETWORK=0
NETWORKTHREADS=0
CLIENTMAX=1100
```

bulunuyor. `USEASYNCNETWORK` uygulanmamış; `NETWORKTHREADS` okunuyor fakat
network manager tarafından kullanılmıyor. Input ve output ana loop thread'inde
çalışıyor.

`CLIENTMAX=1100` nedeniyle bazı yollar her loop turunda 1100 slotluk diziyi
tarar. Yalnız aktif slotlarda socket işi yapılsa da output aktif sayımı ve slot
iterasyonları sürekli yapılır.

### Input riskleri

- Listener accept döngüsünde geçiş başına bağlantı kabul bütçesi yok.
- Tek seferde çok sayıda pending connection varsa while döngüsü uzun sürebilir.
- Her kabulde IP sayısı tüm state dizisi taranarak hesaplanır.
- `pkts=0`, connection accept işini ölçmez.

Kaynak:
[NetworkManager.cs](../src/SphereNet.Network/Manager/NetworkManager.cs),
yaklaşık `204–287`.

### Output riskleri

- 128'den az aktif bağlantıda flush seri yapılır.
- Game socket'leri non-blocking send'e geçer.
- Login/unknown socket send yolu bloklayan `Socket.Send` kullanabilir.
- Log en yavaş client, queue byte veya connection type bilgisini yazmaz.

Kaynaklar:

- [NetworkManager.cs](../src/SphereNet.Network/Manager/NetworkManager.cs),
  yaklaşık `688–723`
- [NetState.cs](../src/SphereNet.Network/State/NetState.cs), yaklaşık `535–703`

### Sonuç

`net_in` ve `net_out` stall'ları için network hâlâ araştırılmalıdır; ancak
mevcut örnekler bir yavaş gameplay packet handler'ını göstermiyor.

---

## 9. Otomatik multicore worker sayısı ana thread'i aç bırakabilir

### Durum

Kesin ayar; canlı olayla bağlantısı olası.

### Kod davranışı

`MulticoreWorkerCount=0`, `Environment.ProcessorCount` kadar worker kullanılmasını
sağlar. Aktif sektör sayısı 50 ve üzerindeyse `Parallel.ForEach` devreye girer.

Kaynaklar:

- [Program.Tick.cs](../src/SphereNet.Server/Program.Tick.cs), yaklaşık `607–648`
- [GameWorld.cs](../src/SphereNet.Game/World/GameWorld.cs), yaklaşık `1280–1299`

### Risk

Dedicated fiziksel makinede bütün logical core'ları kullanmak kabul edilebilir
olabilir. Paylaşımlı veya overcommit sanal makinede:

- Worker'lar ana thread ile CPU için yarışabilir,
- `Thread.Sleep(0)` sonrasında ana thread geç dönebilir,
- ThreadPool işleri, SQLite ve diğer servisler ek rekabet yaratabilir,
- `ProcessorCount` hostun gerçekten garanti ettiği CPU miktarını temsil
  etmeyebilir.

### Kod yazmadan doğrulama

Staging/canary ortamında aynı yükle:

- `MulticoreWorkerCount=0`
- `MulticoreWorkerCount=max(1, logicalCoreCount-1)`
- Düşük çekirdekli VPS için sabit `1` veya `2`

ayrı ayrı denenmeli; p95/p99, `yield`, `snapshot` ve toplam CPU karşılaştırılmalı.

---

## 10. Tick yield stratejisi sistem baskısını görünür hâle getiriyor

### Durum

Kesin.

### Kod davranışı

`TickSleepMode=2`:

```text
SpinWait(100) + Sleep(0)
```

uygular.

Bu mod normal koşulda düşük latency sunabilir. CPU doygunken `Sleep(0)` ana
thread'in uzun süre yeniden planlanmamasına neden olabilir.

### Önemli yorum

Burada `yield` kök neden olmak zorunda değildir. Çoğunlukla makinedeki scheduler
baskısını görünür hâle getiren yerdir. Yalnız `TickSleepMode` değiştirip sistem
baskısını gizlemek, CPU starvation veya ağır background işi çözmez.

### Tanısal A/B

Yalnız kısa ve kontrollü staging testinde:

- Mod 2 mevcut baseline,
- Mod 0 spin testi,
- Mod 1 `Sleep(1)` testi

karşılaştırılabilir.

Mod 0 CPU kullanımını ciddi artırabilir; üretimde körlemesine kalıcı
uygulanmamalıdır.

---

## 11. Telemetride ölçülmeyen veya yanlış gruplandırılan işler var

### Durum

Kesin.

Multicore tick'te aşağıdaki işler `apply` ölçümü bittikten sonra,
`flush` ölçümü başlamadan önce çalışıyor:

- Replay overlays,
- StateRecorder tick,
- Macro engine tick,
- NPC'leri timer wheel'e yeniden schedule etme.

Toplam server tick süresine dahil olmalarına rağmen ayrı bir phase alanları
yoktur. Çok uzun sürerlerse `GetDominantTickPhase()` ölçülmüş diğer alanlardan
en büyüğünü seçebilir ve gerçek pahalı iş başka yerde olduğu hâlde yanlış bir
dominant ad yazabilir.

Bu rapordaki `snapshot=257ms` olayı ölçülen snapshot alanının kendisi gerçekten
257 ms olduğu için bu özel olayda sınıflandırma doğrudur. Ancak diğer
rate-limit nedeniyle ayrıntısı görünmeyen tick'lerde bu telemetri boşluğu önemlidir.

---

## Şu anda ana neden olarak görünmeyen ihtimaller

### GC

`23:07` loop stall satırlarının tamamında:

```text
gc0=+0 gc1=+0 gc2=+0
```

görülüyor. Bu nedenle o loop turlarındaki yüzlerce milisaniyelik beklemeler
managed GC koleksiyonuyla açıklanamıyor.

Save sırasında Gen0 +6 ve Gen1 +4 görülmesi normal snapshot/serialization
tahsis baskısını doğruluyor. Save stall'ının bir kısmı GC olabilir; fakat
`23:07` kümesinin ana nedeni değildir.

### Yavaş gameplay packet handler

Uzun `net_in` örneklerinde:

```text
pkts=0 slowest_pkt=0x00@0,0ms
```

bulunuyor. Kayıtlı handler gövdesi çalışmadığı için çift tıklama, movement,
speech, target veya gump handler'larından biri bu örneklerin doğrudan nedeni
değildir.

Bu yalnız handler'ları eler; accept, receive, encryption initialization ve OS
preemption ihtimallerini elemez.

### NPC compute/apply

Ayrıntılı slow tick'lerde:

```text
compute=0,0ms
npc_build=0,0ms
npc_apply=0,0ms
view_build=0,0ms
```

görülüyor. Bu iki örnek için NPC karar oluşturma, NPC apply ve client view build
ana neden değildir.

Rate-limit nedeniyle ayrıntısı olmayan `23:07:05` ve `23:07:11` olaylarında aynı
sonuç kesin olarak söylenemez.

### Save işleminin 23:07 kümesini doğrudan oluşturması

Save `22:59:52` içinde tamamlanmış ve loop stall `jobs` alanında açıkça
görünmüştür. `23:07` olaylarında `jobs` yaklaşık sıfırdır. Bu nedenle yedi dakika
sonraki küme doğrudan aynı save çağrısı değildir.

Save'in oluşturduğu disk cache, antivirüs taraması veya arka plan yedekleme
sonradan etkide bulunmuş olabilir; fakat bu yalnız ek sistem telemetrisiyle
kanıtlanabilir.

---

## En olası kök neden ağacı

```text
Oyuncunun gördüğü 100–880 ms donma
|
+-- Kesin: ana-loop senkron world save
|   |
|   +-- full-world snapshot/serialization
|   +-- Text format
|   +-- üç shard işi bitene kadar Task.WaitAll
|   +-- account/server-data yazımı
|
+-- Yüksek olasılık: CPU scheduler / host kaynak baskısı
|   |
|   +-- Sleep(0) dönüşü 176–279 ms
|   +-- paket yokken net_in 136–198 ms
|   +-- farklı fazlarda rastgele duvar-saati kaybı
|   +-- multicore worker'ların bütün logical core'ları kullanması
|   +-- başka process, VM overcommit, power plan, antivirüs
|
+-- Motor içinde baskıyı büyüten işler
|   |
|   +-- TIMERF: her tick full-world scan
|   +-- StateRecorder: her tick full-world ToArray
|   +-- sleeping-sector maintenance: 3 dakikada toplu item tick
|   +-- decay catch-up: 5 saniyede full-world scan
|   +-- yoğun aktif sektör ve NPC wake-up
|
+-- Olası network alt nedenleri
    |
    +-- sınırsız accept döngüsü / connection flood
    +-- her accept'te state dizisi üzerinden IP sayımı
    +-- login/unknown socket için blocking send
    +-- ana-loop üzerinde senkron input/output
```

---

## Kod yazmadan uygulanacak teşhis planı

## Aşama 1 — Aktif çalışma ortamını doğrula

Repository config'i ile çalışan publish config'i aynı kabul edilmemelidir.
Canlı log zaten `BinaryGz` yerine `Text` çalıştığını gösteriyor.

Kaydedilecek bilgiler:

- Çalıştırılan `.exe`/`.dll` tam yolu,
- Çalışma dizini,
- Yüklenen `sphere.ini` tam yolu,
- Aktif `SAVEFORMAT`,
- Aktif `SAVESHARDS`,
- `SAVEPERIOD`,
- `CLIENTMAX`,
- `MAXPACKETSPERTICK`,
- `TickSleepMode`,
- `ServerTickMs`,
- `MulticoreWorkerCount`,
- `StateRecordingEnabled`,
- `StateRecordPlayersOnly`,
- Makinenin logical core sayısı,
- Fiziksel makine mi, VPS mi olduğu,
- Save ve `state_recording.db` disklerinin türü.

Beklenen ilk bulgu: Logdaki `format=Text` değerinin hangi config/runtime
değişikliğinden geldiği açıklanmalıdır.

## Aşama 2 — Tam log penceresini koru

Yalnız warning satırları değil, `23:06:30–23:08:00` aralığındaki bütün
Information/Warning/Error satırları alınmalıdır.

Özellikle aranacak satırlar:

- `Connection #...`
- `IP limit`
- `idle timeout`
- `disconnected`
- `send backpressure`
- `slow_packet`
- `StateRecorder cleanup`
- `StateRecorder flush failed`
- `Multicore tick timeout`
- `falling back to single-thread`
- `respawn_full`
- `static_door`
- Admin/audit komutları
- Script error veya timer callback uyarıları

Bu pencere accept flood, login socket, cleanup, admin job ve multicore fallback
olasılıklarını hızlı biçimde ayırır.

## Aşama 3 — Runtime status örneklerini topla

Web status varsayılan olarak oyun portu `2593 + 2 = 2595` üzerinde çalışır.
Yerel makinede:

```powershell
Invoke-RestMethod http://localhost:2595/status
```

çıktısı alınabilir.

En az 15–30 dakika, beş saniyede bir şu alanlar kaydedilmelidir:

- `characters`
- `items`
- `connections`
- `memoryMB`
- `runtime.AvgMs`
- `runtime.P50Ms`
- `runtime.P95Ms`
- `runtime.P99Ms`
- `runtime.MaxMs`
- `runtime.MaxSinceStartMs`
- `runtime.MulticoreEnabled`
- `runtime.SlowTickCount`
- `runtime.LastSlowTickDominantPhase`
- `runtime.Maps[*].ActiveSectors`
- `runtime.Maps[*].OnlinePlayers`

Bir saniyeden daha sık status sorgusu önerilmez. Teşhis aracının kendisi ek
yük oluşturmamalıdır.

## Aşama 4 — Debug tick/GC penceresi al

`[tick_stats]` ve `[gc_stats]` satırları Debug seviyesindedir. Kısa süreli
teşhis penceresinde log seviyesi Debug yapılarak şu veriler alınmalıdır:

- Tick p50/p95/p99/max,
- Allocation MB/s,
- KB/tick,
- Gen0/1/2 sayıları,
- GC pause yüzdesi,
- Managed heap,
- RSS,
- Düşürülen düşük öncelikli paket sayısı.

DebugPackets ile bütün packet payload logunu açmak gerekmeyebilir. Amaç packet
dökümü değil, tick/GC istatistiğidir. Disk log hacmi takip edilmelidir.

## Aşama 5 — İşletim sistemi sayaçlarını aynı timestamp ile kaydet

Windows üzerinde en az şu sayaçlar toplanmalıdır:

- SphereNet process `% Processor Time`
- Toplam CPU `% Processor Time`
- `Processor Queue Length`
- Process thread count
- Process working set/private bytes
- Process I/O read/write bytes/sec
- Disk active time
- Disk average read/write latency
- Available memory
- Context switches/sec
- .NET ThreadPool queue length
- .NET allocation rate
- .NET GC pause time

Mümkünse:

```text
dotnet-counters monitor --process-id <PID> System.Runtime
```

ile kısa bir kayıt alınmalıdır.

VPS kullanılıyorsa sağlayıcının host CPU steal/ready time metriği ayrıca
istenmelidir. Windows guest içindeki toplam CPU düşük görünürken host scheduler
gecikmesi yaşanabilir.

## Aşama 6 — Tek değişkenli A/B testleri

Her test aynı world save, aynı oyuncu/bot sayısı, aynı konum ve aynı süreyle
yapılmalıdır. Aynı anda yalnız bir değişken değiştirilmelidir.

### Test A — StateRecorder

1. `StateRecordingEnabled=1` ile 15 dakika baseline.
2. Staging üzerinde `StateRecordingEnabled=0` ile 15 dakika.
3. p95/p99, allocation, GC, snapshot ve scheduler stall sayısını karşılaştır.

İyileşme varsa StateRecorder full-world allocation ve SQLite işi
önceliklendirilmelidir.

### Test B — Multicore worker sayısı

1. `MulticoreWorkerCount=0`
2. `logicalCoreCount-1`
3. Düşük kaynaklı VPS için `1` veya `2`

Karşılaştırılacaklar:

- Toplam CPU,
- `snapshot` p95/max,
- `yield` stall sayısı,
- Ortalama tick,
- Oyuncu ping/jitter.

Worker azaltınca ortalama tick biraz yükselirken büyük `yield` sıçramaları
düşüyorsa ana thread starvation doğrulanmış olur.

### Test C — Yield modu

Kısa staging testi:

1. `TickSleepMode=2`
2. `TickSleepMode=0`
3. Gerekirse `TickSleepMode=1`

Mod 0 ile `yield` stall kayboluyor fakat CPU %100'e yaklaşıyorsa scheduler
baskısı doğrulanır. Bu, mod 0'ın otomatik olarak kalıcı çözüm olduğu anlamına
gelmez.

### Test D — Ağ izolasyonu

1. Staging sunucusunu dış dünyaya kapat; yalnız tek test istemcisi bağlansın.
2. Aynı world ve aynı bot/oyuncu yüküyle ölç.
3. `net_in/net_out` stall'ları kaybolursa connection scanner/flood/yavaş
   istemci ihtimalini incele.

Canlı ortamda firewall değişikliği kontrollü bakım penceresi dışında
yapılmamalıdır.

### Test E — Bölge yoğunluğu

1. Oyuncu boş bir bölgede sabit beklesin.
2. Yoğun vendor/spawn/item bölgesine girsin.
3. Birden fazla oyuncu birbirinden uzak haritalara dağılsın.

`runtime.Maps.ActiveSectors`, `snapshot` ve tick p99 birlikte
karşılaştırılmalıdır.

### Test F — Save formatı

Aynı staging world üzerinde:

- `Text`
- `Binary`
- `BinaryGz`

formatlarında en az üçer save alınmalıdır.

Ölçülecekler:

- Main-loop stall,
- Snapshot oluşturma süresi,
- Dosya yazım süresi,
- Save dosya boyutu,
- CPU,
- Disk write latency,
- Restore/load süresi.

Yalnız en kısa save süresine göre karar verilmemelidir; restore güvenilirliği ve
uyumluluk testi de geçmelidir.

---

## Önerilen geliştirme planı

Bu bölüm uygulanacak işleri tanımlar; bu incelemede bu işler için kaynak kod
yazılmamıştır.

## P0 — Teşhis telemetrisini güvenilir hâle getir

1. `snapshot` grubunu alt fazlara böl.
2. Her loop için process CPU zamanı ile wall-clock zamanını birlikte ölç.
3. `scheduler_gap = wall_time - process_cpu_time` benzeri bir sinyal ekle.
4. `loop_stall` satırına:
   - Active connection,
   - O turda kabul edilen connection,
   - Okunan/gönderilen byte,
   - En yavaş input state,
   - En yavaş output state,
   - Output queue byte
   ekle.
5. `slow_tick` rate-limit süresince bastırılan olayların count/max bilgisini
   sonraki loga ekle.
6. Sleeping maintenance çalıştı mı ve kaç sector/item işledi, ayrı logla.
7. StateRecorder scan/queue/flush/cleanup sürelerini ayrı ölç.

Başarı koşulu: Bir sonraki `snapshot=250ms` olayında hangi alt işlemin ne kadar
sürdüğü tek log satırından görülebilmeli.

## P0 — Save'i oyuncu thread'inden ayır

Hedef mimari:

1. Ana thread'de tutarlı immutable snapshot yakala.
2. Snapshot'ın dosya serialization ve disk yazımını background worker'a ver.
3. Aynı anda ikinci save'i engelle veya kuyruğa al.
4. Save sırasında değişen objeler için version/dirty takip mekanizması kullan.
5. Atomic temp file + commit + backup davranışını koru.
6. Server shutdown ve save failure senaryolarını açıkça yönet.

Alternatif olarak full snapshot da büyükse:

- Tick başına zaman/obje bütçeli incremental capture,
- Copy-on-write/versioned record,
- Sektör bazlı kademeli save

tasarlanmalıdır.

Başarı koşulu: 50–100 bin objede save sırasında main-loop p99 artışı kabul
edilen tick bütçesini aşmamalı; save/load round-trip kayıp üretmemeli.

## P1 — TIMERF için due-time index kullan

1. TIMERF eklenince merkezi scheduler'a kaydet.
2. Silinen veya yeniden planlanan girdileri güvenli iptal et.
3. Her tick yalnız zamanı gelen girdileri çek.
4. Save/load sırasında remaining-time bilgisini yeniden schedule et.
5. Aynı objede birden fazla TIMERF ve callback sırasında yeniden planlama
   testleri ekle.

Başarı koşulu: Aktif TIMERF sayısı sıfırken maliyet world obje sayısından
bağımsız olmalı.

## P1 — StateRecorder çağrı maliyetini düzelt

1. Interval dolmadan world snapshot üretme.
2. `playersOnly=1` iken yalnız player collection kullan.
3. Character snapshot ihtiyacında bütün item+character objelerini kopyalama.
4. Movement ve state snapshot taramalarını aynı enumerable üzerinde güvenli
   biçimde planla.
5. SQLite flush ile cleanup'ı tek writer kuyruğu üzerinde serileştir.
6. Queue length ve dropped/lagging record metriği ekle.

Başarı koşulu: StateRecorder açık fakat scan zamanı gelmemiş tick'te allocation
yaklaşık sıfır olmalı.

## P1 — Sleeping maintenance işini bütçele

1. Map/sector cursor tut.
2. Tick başına süre veya item sayısı bütçesi belirle.
3. Üç dakikalık periyotta bütün işi tek tick'e yığma.
4. Spawn, decay ve normal item timer önceliklerini ayır.
5. Bakımın en geç tamamlanma süresi için deadline belirle.

Başarı koşulu: Maintenance açıldığında tek bir tick'in p99/max değerinde
belirgin sıçrama olmamalı.

## P1 — Network ana-loop bloklama risklerini kapat

1. Accept geçişine connection/time bütçesi ekle.
2. IP başına connection sayısını her accept'te tüm state dizisini taramadan
   sayaçla yönet.
3. Kabul edilen socket'i ilk andan itibaren non-blocking yap.
4. Login/unknown send yolunu non-blocking output buffer'a taşı.
5. En yavaş client flush süresini ve connection type'ını logla.
6. Input/output için toplam byte ve pass bütçesi uygula.
7. Async network veya adanmış network thread mimarisini davranış testleriyle
   değerlendir.

Başarı koşulu: Paket göndermeyen connection flood altında oyun tick p99'u
kontrollü kalmalı ve ana thread accept döngüsüne hapsolmamalı.

## P2 — Decay için tam-dünya taramasını kaldır

1. DecayTime atanan ground item'ı due queue/index'e ekle.
2. Item taşınır, contain edilir veya silinirse index'i güncelle.
3. Beş saniyede full-world scan yerine zamanı gelen en fazla bütçeli sayıda
   item işle.
4. `NODECAY` region ve corpse özel davranışlarını koru.

Başarı koşulu: Hiç expired item yokken decay bakım maliyeti world size'dan
bağımsız olmalı.

## P2 — Ana thread için CPU payı ayır

1. Otomatik worker sayısında ana thread için en az bir logical core payı bırakmayı
   değerlendir.
2. Paylaşımlı VPS için açık worker profilleri sun.
3. ThreadPool starvation metriği ekle.
4. Gerekirse ana loop thread priority değişikliğini yalnız benchmark ile
   değerlendir; `RealTime` priority kullanılmamalı.
5. Windows power plan ve CPU affinity yalnız ölçümlü operasyon ayarı olarak
   ele alınmalı.

Başarı koşulu: Yoğun multicore tick sırasında `yield` dönüşlerinde yüzlerce
milisaniyelik scheduler boşlukları oluşmamalı.

## P2 — Aktif sektör yoğunluk telemetrisi ekle

Her yavaş world tick için:

- Aktif sector sayısı,
- Yeni uyanan sector sayısı,
- Aktif character/item sayısı,
- En yoğun sector ve sayıları,
- Uyandırılan NPC sayısı,
- NoSleep sector sayısı

raporlanmalıdır.

Başarı koşulu: Yoğun lokasyon kaynaklı tick spike tek logla bulunabilmeli.

---

## Performans test matrisi

| Senaryo | Süre | Oyuncu/bağlantı | Beklenen ölçüm |
|---|---:|---:|---|
| Boş dünya, dış ağ kapalı | 15 dk | 0/0 | Motor taban maliyeti |
| Mevcut world, dış ağ kapalı | 15 dk | 0/0 | Full-world scan tabanı |
| Tek oyuncu, boş bölge | 15 dk | 1/1 | Minimum aktif sektör maliyeti |
| Tek oyuncu, yoğun şehir | 15 dk | 1/1 | Sector density etkisi |
| Oyuncular aynı bölgede | 15 dk | hedef yük | Çakışan active-sector penceresi |
| Oyuncular farklı bölgelerde | 15 dk | hedef yük | Maksimum active-sector sayısı |
| Connection flood simülasyonu | 5 dk | kontrollü | Accept/input dayanıklılığı |
| Yavaş login istemcisi | 5 dk | kontrollü | Blocking output dayanıklılığı |
| StateRecorder açık/kapalı | 15+15 dk | aynı | Allocation ve SQLite etkisi |
| Worker auto/sabit | 15+15 dk | aynı | Scheduler starvation |
| Text/Binary/BinaryGz save | 3 tekrar | aynı world | Save stall ve restore |
| Üç dakikalık maintenance | en az 10 dk | aynı | Periyodik snapshot spike |
| TIMERF yoğunluğu 0/orta/yüksek | 10 dk | aynı | Scheduler ölçeklenmesi |

Her koşuda saklanacak sonuç:

```json
{
  "scenario": "single-player-dense-city",
  "durationMinutes": 15,
  "hardware": {
    "environment": "physical-or-vps",
    "logicalCores": 0,
    "ramGB": 0,
    "disk": "type"
  },
  "world": {
    "items": 46207,
    "characters": 6374,
    "connections": 0,
    "activeSectors": 0
  },
  "tick": {
    "avgMs": 0,
    "p50Ms": 0,
    "p95Ms": 0,
    "p99Ms": 0,
    "maxMs": 0
  },
  "stalls": {
    "loopCount": 0,
    "slowTickCount": 0,
    "worstLoopMs": 0,
    "worstTickMs": 0,
    "dominantPhase": ""
  },
  "runtime": {
    "cpuAveragePercent": 0,
    "cpuPeakPercent": 0,
    "allocationMBPerSec": 0,
    "gcPausePercent": 0,
    "threadPoolQueuePeak": 0,
    "diskLatencyPeakMs": 0
  }
}
```

---

## Kabul kriterleri

Donanıma ve hedef oyuncu sayısına göre eşikler yeniden kalibre edilmelidir.
Başlangıç hedefi olarak:

- Normal oyunda tick p95 `< 25 ms`,
- Tick p99 `< 50 ms`,
- Save dışı loop p99 `< 50 ms`,
- Tekil normal gameplay tick `< 100 ms`,
- `yield > 100 ms` olaylarının sıfıra yakın olması,
- Paket yokken tekrarlayan `net_in > 100 ms` olmaması,
- Save sırasında ana-loop durmasının hedef mimaride `< 50 ms` olması,
- Gen2 ve GC pause yüzdesinin düşük/istikrarlı olması,
- StateRecorder interval dışı tick allocation'ının world boyutundan bağımsız
  olması,
- Sleeping maintenance'in tek tick'te `SlowTickWarnMs` aşmaması,
- Save/load round-trip'te item, character, house, ship, guild ve timer kaybı
  olmaması

önerilir.

---

## Bir sonraki inceleme için gerekli canlı veriler

Kesin kök nedeni ayırmak için şu veriler yeterli olacaktır:

1. `23:06:30–23:08:00` tam logu,
2. Aynı anda alınmış `/status` JSON örnekleri,
3. O pencerenin CPU, disk latency ve process thread sayaçları,
4. Aktif publish `sphere.ini`,
5. Sunucunun fiziksel/VPS bilgisi ve logical core sayısı,
6. O anda online oyuncu ve aktif bağlantı sayısı,
7. `state_recording.db` dosya boyutu,
8. Sunucunun dış dünyaya açık olup olmadığı,
9. Oyuncuların o anda yoğun bir bölgeye girip girmediği,
10. Aynı pencereye denk gelen antivirüs/backup/güncelleme işi olup olmadığı.

---

## Nihai değerlendirme

Bu log performans sorunu olmadığını söyleyecek kadar hafif değildir:

- `483 ms` save durması kesin ve oyuncuya görünürdür.
- `261 ms`, `257 ms` ve özellikle `880 ms` tick gecikmeleri ciddidir.
- `176–279 ms` yield gecikmeleri, yalnız oyun mekaniklerinden bağımsız bir
  scheduler/host baskısını güçlü biçimde gösterir.
- Paket işlenmeden `136–198 ms` net input görülmesi, yavaş packet handler
  hipotezini zayıflatır.
- 52 bin objeli dünyada her tick veya sık periyotlarla yapılan full-world
  taramalar, bu baskı altında gecikmeleri büyüten gerçek altyapı borçlarıdır.

Önerilen sıra:

1. Önce aktif config ve işletim sistemi sayaçlarıyla scheduler/host baskısını
   doğrula.
2. Tam log penceresiyle connection flood, login socket ve StateRecorder cleanup
   ihtimallerini ayır.
3. `snapshot` alt faz telemetrisini ayrıştır.
4. Save'i ana loop dışına çıkaracak tutarlı mimariyi tasarla.
5. TIMERF, StateRecorder, sleeping maintenance ve decay tam-dünya taramalarını
   due-time/bütçeli yapılara taşı.
6. Network accept/login output yollarını bütçeli ve non-blocking hâle getir.
7. Bütün değişiklikleri aynı world ve aynı yükle p95/p99 temelli A/B test et.

Bu sıralama uygulanmadan yalnız warning eşiklerini yükseltmek veya logları
kapatmak, gecikmeyi çözmez; yalnız görünmez hâle getirir.
