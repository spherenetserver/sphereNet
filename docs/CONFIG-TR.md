# SphereNet Konfigürasyon Başvurusu (sphere.ini)

`config/sphere.ini` dosyasının tüm anahtarları; Source-X `CServerConfig` karşılığı. Ini
dosyasında her anahtar için tek satırlık bir özet vardır; tam açıklama bu dosyadadır.
İngilizce sürüm: [CONFIG-EN.md](CONFIG-EN.md).

## Sözdizimi

- `ANAHTAR=DEĞER`. Anahtar adlarında büyük/küçük harf farkı yoktur.
- Yorumlar `//` ile başlar. Satır içinde `//` sonrası da yorumdur; bu yüzden bir değere
  `//` yazılamaz (örn. URL'de `https://` kullanılamaz).

#### Sayılar

Anahtarın nasıl okunduğuna göre üç kural:

1. Çoğu tamsayı anahtarı **ondalık** okunur; onaltılık için `0x` öneki gerekir (`0x20`).
   Bu anahtarlarda Sphere usulü baştaki sıfır onaltılık **değildir**: `0200` = iki yüz;
   `|` ile yazılmış bir değer (`01|010`) okunamaz ve varsayılan kalır. `COMBATFLAGS`,
   `MAGICFLAGS`, `OPTIONFLAGS`, `CHATFLAGS`, `RACIALFLAGS`, `EMOTEFLAGS`, `LOGMASK` ve
   `FEATURE*` bu gruptadır.
2. Bayrak anahtarları (`REVEALFLAGS`, `AREAFLAGS`, `AUTOPRIVFLAGS`, `STATSFLAGS`,
   `EXPERIENCEMODE`, `COMBATPARRYINGERA`) Sphere kuralıyla okunur: **baştaki sıfır
   onaltılıktır** (`010` = 16) ve `|` ile birden çok değer OR'lanır (`01|02|010`).
   `MINCHARDELETETIME`, `REGEN3` ve `TIMEOUTINCOMPLETECONN` de bu kuralla okunur ve çarpım
   olarak yazılabilir (`7*24*60*60`, `5*1000`).
3. `NPCAI`, `PETSINHERITNOTORIETY`, `ERALIMIT*`, `TELEPORTEFFECT*`, `TELEPORTSOUND*`,
   `WOPCOLOR`, `WOPFONT`, `WOPTALKMODE`: `0x` öneki veya baştaki sıfır onaltılık, diğerleri
   ondalık. `COLOR*` anahtarları **her zaman** onaltılıktır (`0x`'siz de: `22` = `0x22`).

#### Boolean

`1`, `true` veya `yes` = açık; başka her şey (`0` ve `2` dahil) = kapalı.

## Durum etiketleri

`sphere.ini` içinde her anahtarın satırı bunlardan biriyle biter:

- `[WORKING]` (çalışıyor) - kod tarafından aktif olarak kullanılıyor.
- `[READ ONLY]` (okunuyor) - config'e yükleniyor ama oyun davranışını değiştirmiyor (en
  fazla bir script `SERV.<ANAHTAR>` ile geri okuyabilir).
- `[NOT IMPLEMENTED]` (uygulanmadı) - config'e bile yüklenmiyor; Source-X ini uyumu için
  duran yer tutucu. Sunucu açılışta bunları "bu motor okumuyor" diye listeler.

Aşağıdaki "Varsayılan", anahtar hiç yazılmazsa kullanılan kodun kendi değeridir. Dağıtılan
`sphere.ini` içindeki etkin değer bilerek ondan farklı olabilir.

## HATA AYIKLAMA & LOG

### DEBUGPACKETS

Paket trafiğini konsola/log'a döker ve konsol seviyesini Debug'a indirir. Çalışırken panelden de açılıp kapatılabilir.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `0`

### SCRIPTDEBUG

Script yürütme izlerini loglar. Panelden de açılıp kapatılabilir.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `0`

### DEBUGPACKETOPCODES

Opsiyonel paket debug filtresi: hex opcode listesi, virgülle. Boşsa tüm paketler loglanır (`0x73` ping hariç).

- **Tür:** string (örn. `22,34,BF` = yalnızca resync/status/extended)
- **Varsayılan:** `""` (hepsi)

### DEBUGPACKETCATEGORIES

Paket log'unda hangi tür paketlerin tutulacağı.

- **Tür:** `player`, `npc`, `item`, `packet` (virgülle ayrılır); boş = hepsi
- **Varsayılan:** `""`
- **Not:** Kalabalık bir sokak, oyuncuyla ilgili olandan çok daha fazlasını yaratıklar ve eşyaları hakkında gönderir; debug koşusunda log'un da maliyetinin de büyük kısmı odur. `player,packet` istemcinin sunucuyla kendi konuşmasını okunur bırakır, gerisini biçimlendirmeden atar.

### LOGFILELEVEL

`logs/spherenet-*.log` dosyasının filtresi. Konsol seviyesi bundan bağımsızdır (`DebugPackets`'e uyar); bu yalnızca dosyaya yazılanı etkiler, rutin INF mesajları (komutlar, script yüklemeleri, bağlantılar) diske gitmez.

- **Tür:** iki sözdizimi:
  1. Tek değer = en düşük eşik; o seviye ve üstü yazılır.
     ```
     LogFileLevel=Warning      -> WRN, ERR, FTL
     LogFileLevel=Error        -> yalnızca ERR + FTL
     LogFileLevel=Information  -> konsoldakiyle aynı
     ```
  2. `|` veya virgülle beyaz liste = YALNIZCA listedeki seviyeler yazılır.
     ```
     LogFileLevel=Verbose | Warning | Error | Fatal
     LogFileLevel=Error, Fatal
     ```
  Seviye adları (büyük/küçük harf fark etmez): `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`.
- **Varsayılan:** `Warning` (yalnızca WRN/ERR/FTL diske gider)
- **Not:** Serilog dosya sink filtresi olarak uygulanır.

## PERFORMANS & MULTICORE

Multicore tick hattı her zaman açıktır: Build (paralel) -> Apply (seri). Hata veya zaman aşımında otomatik olarak tek thread'e düşer; bunun için bir anahtar yoktur.

### TICKSLEEPMODE

Tick'ler arasında ana döngünün CPU'yu nasıl bıraktığı.

- **Tür:** integer (0-3)

  | Değer | Mod | Davranış |
  |---|---|---|
  | `0` | spin | En düşük gecikme (<1ms), en yüksek CPU; adanmış sunucular için. |
  | `1` | sleep | Minimum CPU, ~2ms gecikme; zayıf/paylaşımlı makineler için. |
  | `2` | hybrid | Dengeli, ~1ms gecikme, orta CPU. |
  | `3` | adaptive | Bir sonraki tick'e kadar kalan süreyi sınırlı dilimlerle uyur: boştayken CPU ~0, giriş gecikmesi sınırlı; geçişten önce/sonra `[tick_stats]` lpt ölçüsünü karşılaştırın. |

- **Varsayılan:** `3`
- **Not:** Windows'ta sunucu 1 ms sistem zamanlayıcısı ister ve güç kısıtlamasından çıkar; 1 ve 3 modlarının uykusu, pencere gizliyken bile 15,6 ms yerine ~1-2 ms sürer.

### ASYNCRECEIVE

Bağlantıların veriyi nasıl aldığı.

- **Tür:** boolean (0-1)
  - `1` = completions: her bağlantıda bekleyen bir alım vardır; veri ana döngü dışında toplanır ve gelir gelmez döngünün uykusu biter (bağlantı başına yoklama yok, daha düşük giriş gecikmesi).
  - `0` = polled: ana döngü her turda hangi bağlantıda veri olduğunu işletim sistemine sorar (hepsi için tek `Socket.Select`). Yedek yol.
- **Varsayılan:** `1`
- **Not:** Source-X `USEASYNCNETWORK` değildir; o gönderme ile ilgilidir.

### SLOWTICKWARNMS / LOOPSTALLWARNMS

Tanı eşikleri (ms). `SlowTickWarnMs`'ten yavaş bir sunucu tick'i `[slow_tick]`, `LoopStallWarnMs`'ten yavaş bir ana döngü turu `[loop_stall]` uyarısı loglar. `0` = o uyarı kapalı.

- **Tür:** integer (ms)
- **Varsayılan:** `50` / `100` (100ms tick bütçesinin yarısı / tamamı)

### SLOWPACKETWARNMS

Tek bir gelen paketin handler'ı bu eşikten uzun sürerse opcode ve handler adıyla `[slow_packet]` loglanır; `net_in` kaynaklı bir `[loop_stall]`'un hangi paketten geldiğini söyler.

- **Tür:** integer (ms, `0` = kapalı)
- **Varsayılan:** `20`

### MULTICOREWORKERCOUNT

Paralel fazlarda kullanılacak iş parçacığı sayısı.

- **Tür:** integer (`0` = otomatik, ProcessorCount)
- **Varsayılan:** `0`
- **Not:** `Parallel.ForEach` MaxDegreeOfParallelism değeri olarak uygulanır.

### MULTICOREPHASETIMEOUTMS

Bir multicore fazının zaman aşımı (ms).

- **Tür:** integer (ms, en az 100)
- **Varsayılan:** `5000`
- **Not:** Aşıldığında çalışma bayrağı kapanır ve tick tek thread'e döner. 100'ün altı açılışta uyarı basar. Zaman aşımı işbirlikçidir: sektörler, dünya tick fazları ve NPC/görüş fazları arasında kontrol edilir; çalışan iş kesilmez, bu yüzden bir tick süreyi o işin uzunluğu kadar aşabilir.

### MAXNPCSPERTICK

Tick başına işlenen NPC eylemi; fazlası sonraki tick'e kalır (`[npc_budget]` log satırı).

- **Tür:** integer (en az 1)
- **Varsayılan:** `500`

### NPCPATHPRESTAGEPERTICK

Tick başına kabul edilen ön A* yol araması; diğer takipçiler ertelenir ve sonraki tick'lerde sırayla alınır.

- **Tür:** integer (en az 0)
- **Varsayılan:** `2`

### MULTICOREDETERMINISMDEBUG

Determinizm doğrulama modu: her tick sonunda dünyanın SHA-256 hash'i hesaplanıp loglanır (ilk 2000 tick).

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `0`

### MULTICOREDETERMINISMEXPECTEDHASH

Beklenen determinizm hash'i; uyuşmazlıkta uyarı loglanır. Boş = karşılaştırma yok.

- **Tür:** string (SHA-256 hex)
- **Varsayılan:** `""`

## SUNUCU KİMLİĞİ, PANEL & HOST

### SERVNAME

Sunucu listesinde ve durum sayfasında görünen ad (0xA8 sunucu listesi, web durum sayfası, telnet konsol ve panel kullanır).

- **Tür:** string
- **Varsayılan:** `"SphereNet"`

### SERVPORT

Oyun sunucusunun dinlediği TCP portu.

- **Tür:** integer (1-65535)
- **Varsayılan:** `2593`
- **Not:** Standart UO portu 2593. Telnet konsol: `SERVPORT+1`, web durum: `SERVPORT+2`, panel (`ADMINPANELPORT=0` iken): `SERVPORT+3`.

### SERVIP

Giriş sunucu listesinde (`0xA8`) ve oyun sunucusuna yönlendirmede (`0x8C`) istemciye bildirilen IP adresi. Scriptlerden `SERV.SERVIP` olarak da okunur.

- **Tür:** string (IPv4)
- **Varsayılan:** `"0.0.0.0"` (= bağlantının geldiği yerel adres, o da yoksa `127.0.0.1`)
- **Not:** Dinlenen adresi DEĞİŞTİRMEZ; soket her zaman `0.0.0.0:SERVPORT` dinler. NAT/yönlendirici arkasındaki bir sunucuda dış IP buraya yazılır.

### SERVERLIST

Giriş ekranındaki sunucu listesine bu shard'a EK olarak konan sunucular.

- **Tür:** string: `;` ile ayrılmış `ad,ip,port` girdileri (port yoksa `SERVPORT`)
- **Varsayılan:** `""` (yalnızca bu shard)
- **Not:** Source-X `[SERVERS]` bloğunun karşılığı; hatalı girdi sessizce atlanır.
- **Örnek:** `SERVERLIST=Test Shard,192.168.1.10,2593;Event Shard,192.168.1.11,2594`

### URL

Shard'ın web sitesi; scriptlere `SERV.URL` olarak verilir.

- **Tür:** string
- **Varsayılan:** `"www.spherenetserver.com"`
- **Not:** Bu dosyada `//` yorum başlattığı için `https://` yazılamaz; şemayı gerekirse script tarafı ekler. Source-X de değeri yalnızca `SERV.URL` ile döndürür.
- **Örnek:** `URL=www.myshard.com`

### ADMINEMAIL

Yönetici e-posta adresi; scriptlere `SERV.ADMINEMAIL` olarak verilir.

- **Tür:** string
- **Varsayılan:** `""`
- **Not:** Yalnızca `SERV.ADMINEMAIL` ile geri okunur; motor bunu kullanmaz.

### ADMINPASSWORD

Telnet yönetim konsolu ve web paneli şifresi (telnet konsol, admin host politikası, SphereNet.Host ve panel kullanır).

- **Tür:** string
- **Varsayılan:** Anahtar HİÇ yazılmazsa `"1234"`; boş değer telnet admin konsolunu kapatır.
- **Not:** Bu dosya boş bırakır; panel kurulum sihirbazı seçilen şifreyi hash'leyip buraya yazar.

### ADMINPANELAUTOFILL

Panel giriş ekranında `ADMINPASSWORD`'u otomatik doldur (`/api/auth/local-hint`).

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `0` (kapalı)
- **Not:** Yalnızca localhost'tan gelen istekte ve `ADMINPASSWORD` düz metin iken çalışır; kurulum sihirbazı şifreyi hash'e çevirince durur. Şifreyi tarayıcıya verdiği için yalnızca yerel geliştirmede açın.

### ADMINPANELPORT

Web panelin dinlediği port (SphereNet.Host ve süreç içi admin paneli kullanır).

- **Tür:** integer
- **Varsayılan:** `0` (= `SERVPORT+3`)

### ADMINPANELALLOWEDHOSTS

Panelin cevap verdiği Host adları (virgülle); isteğin Host başlığına göre kontrol edilir.

- **Tür:** string
- **Varsayılan:** `""` (yalnızca `localhost`, `127.0.0.1`, `::1`)
- **Not:** Panel yalnızca localhost'u dinler. Reverse proxy arkasında proxy'nin ilettiği genel adı buraya yazın (örn. `panel.sunucum.com`). Listede olmayan Host adıyla gelen istek 400 alır: kötü niyetli bir sayfanın kendi alan adını 127.0.0.1'e yönlendirip paneli kullanmasını (DNS rebinding) engeller. `*` kontrolü kapatır.

### PUBLICPAPERDOLL

Karakter paperdoll resmini ve bilgisini girişsiz sunar: `/public/paperdoll/<serial>.png` ve `/public/paperdoll/<serial>.json`.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `0` (kapalı; iki adres de 404)
- **Not:** Serial hex (`0x..` ya da `0` ile başlayan) veya ondalık yazılır. Yalnızca oyuncu karakterleri gösterilir; personel (Counselor ve üstü), NPC, silinmiş ve bilinmeyen serial aynı 404'ü alır. JSON'da plevel/hesap/IP yoktur. `Cache-Control max-age=60`; IP başına dakikada 60 istek. Panel yalnızca localhost'u dinler: dışarıdan erişim reverse proxy ile olur ve proxy'nin Host adı `ADMINPANELALLOWEDHOSTS`'ta olmalıdır.

### PUBLICPAPERDOLLORIGINS

Herkese açık paperdoll'u tarayıcıdan (fetch/XHR) okuyabilecek web siteleri (CORS), virgülle; `Access-Control-Allow-Origin` olarak gönderilir.

- **Tür:** string
- **Varsayılan:** `""` (CORS başlığı gönderilmez; `<img>` etiketi yine çalışır)
- **Not:** Yalnızca alan adı (gerekirse `:port`) yazın, örn. `www.sunucum.com`. `//` yorum başlattığı için `https://` yazılamaz; http ve https ikisi de kabul edilir. `*` her siteye izin verir.

### SCRIPTPACKREPO

Panelin Scripts sayfasındaki "Install" düğmesinin indirdiği GitHub script paketi (`sahip/ad`) (`/api/scripts/download`).

- **Tür:** string
- **Varsayılan:** `Sphereserver/Scripts-X` (resmi Source-X paketi)
- **Not:** Dosyaları depo kökünde ya da `scripts/` klasöründe olan paketlerin ikisi de desteklenir. `_` ile başlayan üst klasörler (`_incomplete` vb.) kurulmaz.

### SCRIPTPACKBRANCH

İndirilecek dal (`/api/scripts/download`).

- **Tür:** string
- **Varsayılan:** `main`

### HOSTAUTOSTART

SphereNet.Host açılınca oyun sunucusunu kendisi başlatsın.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `1`
- **Not:** `0` ise sunucu panelden "Start" ile başlatılır. Güncelleme veya yeniden başlatma sonrası shard'ın kapalı kalmaması için `1` önerilir.

### HOSTRESTARTONCRASH

Sunucu çökerse Host onu yeniden başlatsın.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `1`
- **Not:** Bekleme 5, 10, 20 sn... en fazla 2 dk. 10 dakikada 5 çökmeden sonra durur (açılışta hep çöken sunucu sonsuz döngüye girmesin). Normal kapanış (çıkış kodu 0) ve panelden Stop çökme sayılmaz.

### HOSTSHUTDOWNQUIETMS

Kapanmakta olan sunucu bu kadar süre (ms) hiç çıktı üretmezse Host onu takılmış sayar ve sonlandırır.

- **Tür:** integer (ms, 1000-600000 arasına kırpılır)
- **Varsayılan:** `20000`
- **Not:** Büyük bir dünyanın kapanış kaydı yavaş diskte uzun sürebilir; erken sonlandırma son periyodik kayıttan bu yana olan her şeyi kaybettirir.

### HOSTSHUTDOWNTIMEOUTMS

Kapanış için mutlak üst süre (ms); aşılırsa süreç sonlandırılır.

- **Tür:** integer (ms, 1000-3600000 arasına kırpılır)
- **Varsayılan:** `180000`

## PANEL ÜZERİNDEN GÜNCELLEME (Updates sayfası)

İki ayrı güncelleme yolu var, karıştırmayın:

- `APPUPDATE*` (aşağıdakiler) -> panelin "Check for update" akışı. GitHub Actions'ın ürettiği HAZIR binary'yi indirir. Makinede git/SDK/Node GEREKMEZ. Sunucuyu panelden tek tıkla günceller.
- `APPUPDATEREPODIR` (en altta) -> `update.cmd`'nin kaynaktan derleme akışı. Makinede git + .NET SDK + Node ister ve sunucu KAPALIYKEN çalışır.

### APPUPDATEREPO

Güncellemelerin çekileceği GitHub deposu (`sahip/ad`). SphereNet.Host okur, panel `/api/update/*` altında sunar.

- **Tür:** string
- **Varsayılan:** `""` (boş = panel güncelleme sistemi tamamen kapalı, sayfa gizlenir)
- **Not:** Depo public ise token GEREKMEZ; release asset'leri anonim indirilir.

### APPUPDATECHANNEL

Paketlerin asılı olduğu release tag'i.

- **Tür:** string
- **Varsayılan:** `nightly`
- **Not:** `nightly` her main commit'inde otomatik güncellenen rolling prerelease'tir (bkz. `.github/workflows/release.yml`). Yalnızca elle etiketlenmiş kararlı sürümleri istiyorsanız buraya o tag'i yazın.

### APPUPDATERUNTIME

İndirilecek paketin platformu (RID); asset adını belirler: `spherenet-<RID>.zip`.

- **Tür:** string
- **Varsayılan:** `win-x64`
- **Not:** CI şu an yalnızca win-x64 paketi üretiyor.

### APPUPDATECHECKMINUTES

Arka planda yeni sürüm kontrol aralığı (dakika).

- **Tür:** integer (dakika)
- **Varsayılan:** `15`
- **Not:** `0` = otomatik kontrol kapalı (yalnızca panelden elle "Check for update"). Kontrol ASLA kendiliğinden güncelleme uygulamaz; yalnızca rozet gösterir.

### APPUPDATETOKEN

İsteğe bağlı GitHub token'ı.

- **Tür:** string
- **Varsayılan:** `""` (boş)
- **Not:** Public depoda GEREKSİZ, boş bırakın. Yalnızca depo private yapılırsa gerekir (`contents:read` yetkisi yeter).

### APPUPDATEREPODIR

`update.cmd` (tek komutla güncelleme) için kaynak (git) klasörü. YALNIZCA `update.cmd`/`update.ps1` okur; SphereNet.Host kullanmaz. Bu çalışma klasörü yalnızca publish edilmiş binary'lerden oluşuyorsa (örn. `C:\sphereNet`), burayı bir git klonuna yönlendirin: `update.cmd` o klonda `git pull` + `build.ps1` çalıştırır, sonra üretilen binary'leri buraya kopyalar. O makinede git + .NET SDK + Node/npm kurulu olmalıdır. Göreli yol `sphere.ini`'ye göre çözülür. Çalışma klasörü repo ise boş kalabilir.

- **Tür:** string (klasör yolu)
- **Varsayılan:** `""`
- **Örnek:** `APPUPDATEREPODIR=D:\Projeler\Yunus\sphereNet`

## İSTEMCİ & BAĞLANTI AYARLARI

### CLIENTERA

İstemci sürümünü bildirene kadar kullanılan varsayılan uyumluluk profili.

- **Tür:** `Sphere56x` | `Modern` (veya `0` | `1`)
  - `Sphere56x`: bilinmeyen istemci eski Sphere 56x paket formatlarını kullanır.
  - `Modern`: sürüm gelene kadar modern formatlar (ClassicUO/7.x) varsayılır.
- **Varsayılan:** `Modern`

### CLIENTVERSION

Beklenen istemci sürüm dizgisi.

- **Tür:** string (`major.minor.revision.patch`)
  - `"4.0.2"` - T2A dönemi istemcisi
  - `"5.0.9.1"` - AOS dönemi istemcisi
  - `"7.0.96.5"` - son klasik istemci
- **Varsayılan:** `"4.0.2"`
- **Not:** Yalnızca açılışta loglanır. Her bağlantının istemci sürümü istemcinin kendi bildirdiğinden alınır; bu değerle karşılaştırılmaz.

### CLIENTMAX

Aynı anda bağlanabilecek en fazla istemci sayısı.

- **Tür:** integer (0 veya altı = 256)
- **Varsayılan:** `256`
- **Not:** Her bağlantı bir NetState slotu kullanır; yüksek değer = daha çok bellek.

### CLIENTMAXIP

Tek bir IP adresinden en fazla bağlantı sayısı.

- **Tür:** integer (`0` = sınırsız)
- **Varsayılan:** `16` (Source-X ile aynı)
- **Not:** Sunucu her kabulde bu IP'nin açık bağlantılarını sayar ve sınırda reddeder. Aynı adresten 16'dan fazla kişi oynayacaksa (internet kafe) yükseltin; yoksa 17. kişi meşru olduğu halde reddedilir. Yeniden bağlanan oyuncunun eski bağlantısı toplanana kadar ikisi birlikte sayılabilir; sınırı oyuncu sayısının biraz üstünde tutun.

### CLIENTLINGER

Güvenli çıkış yapmadan bağlantısı kopan oyuncunun karakterinin dünyada kalma süresi (saniye).

- **Tür:** integer (saniye, `0` = hemen çıkar)
- **Varsayılan:** `600` (Source-X ile aynı)
- **Not:** Linkdead süresi — bu sürede yeniden bağlanan oyuncu karakterine döner. Ölüler ve güvenli çıkış (han/ev) beklemez. Source-X de saniye okur.

### CONNECTINGMAX

Giriş yapma aşamasındaki (pre-login) en fazla istemci sayısı.

- **Tür:** integer
- **Varsayılan:** `32`
- **Not:** Yalnızca `SERV.CONNECTINGMAX` ile okunur; ayrı bir "connecting" sınırı uygulanmaz (IP başına sınır `CONNECTINGMAXIP`'tedir).

### CONNECTINGMAXIP

IP başına oyuna henüz girmemiş en fazla bağlantı sayısı.

- **Tür:** integer (`0` = sınırsız)
- **Varsayılan:** `8`
- **Not:** Bunu aşan IP reddedilir (Source-X `CNetworkManager.cpp:159`). Loopback muaftır.

### USECRYPT

Şifreli (Blowfish/Twofish) girişi kabul et.

- **Tür:** boolean (0/1)
  - `0` = yalnızca şifresiz istemci
  - `1` = şifreli istemci (standart UO)
- **Varsayılan:** `1`
- **Not:** Şifreleme seçimini belirler (anahtarlar `sphereCrypt.ini`'den).

### USENOCRYPT

Şifreli bağlantıların yanında şifresiz bağlantıları da kabul et.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`
- **Not:** `USECRYPT` ile birlikte çalışır.

### COLORNOTO*

Tek tıklamada çıkan isim etiketinin notoriety'ye göre rengi.

- **Tür:** hex hue (HER ZAMAN onaltılık, `0x` ile veya `0x`'siz: `22` = kırmızı, `59` = mavi, `3b2` = gri, `90` = turuncu)
- **Varsayılan:** aşağıdaki değerler
- **Not:** `0` = motorun yerleşik varsayılanını kullan. Hue 0 asla "siyah" demek değildir; 0 yazarsanız varsayılan renk uygulanır. Özel renk istemiyorsanız yorumda bırakın; varsayılanlar zaten renklidir.

| Anahtar | Varsayılan | Anlamı |
|---|---|---|
| `COLORNOTOGOOD` | `063` | mavi (masum) |
| `COLORNOTOGOODNPC` | `063` | mavi (masum NPC) |
| `COLORNOTOGUILDSAME` | `044` | yeşil (müttefik) |
| `COLORNOTONEUTRAL` | `03b2` | gri (saldırılabilir) |
| `COLORNOTOCRIMINAL` | `03b2` | gri (suçlu) |
| `COLORNOTOGUILDWAR` | `02b` | turuncu (düşman lonca) |
| `COLORNOTOEVIL` | `026` | kırmızı (katil) |
| `COLORNOTOINVUL` | `035` | sarı (ölümsüz) |
| `COLORNOTOINVULGAMEMASTER` | `0b` | mor (GM) |
| `COLORNOTODEFAULT` | `03b2` | gri (diğerleri) |

Yukarıdaki varsayılanlar Sphere 0.56b'nindir (`CResource.cpp:210-216`). Source-X'in değerleri:

```
COLORNOTOGOOD=059  COLORNOTOGOODNPC=059  COLORNOTOGUILDSAME=03f
COLORNOTOGUILDWAR=090  COLORNOTOEVIL=022
```

### COLOREMOTE

Motorun emote satırlarının rengi ("*You see NAME looks deathly sick*").

- **Tür:** hex hue
- **Varsayılan:** `022` (kırmızı, Sphere 0.56b `HUE_RED`)
- **Not:** Source-X emote'ları gri (`03b2`, `HUE_TEXT_DEF`) gönderir. SphereNet ayarı.

### COLORINVISITEM

GM'in görünmez bir eşyayı gördüğü renk.

- **Tür:** hex hue (`0` = eşyanın kendi rengi, `1000` = görünmez gri)
- **Varsayılan:** `1000`
- **Not:** Yalnızca `SERV.COLORINVISITEM` ile okunur; eşya çiziminde kullanılmıyor.

### COLORINVIS / COLORINVISSPELL / COLORHIDDEN

0 dışı bir değer, o durumdaki karakter için istemcinin gri örtüsünü (mobil bayrak `0x80`) kapatır: GM görünmezliği / invisibility büyüsü veya iksiri / gizlenmiş (Source-X `CCharStatus.cpp:687`). Hue'nun kendisi SphereNet'te çizime uygulanmaz.

- **Tür:** hex hue
- **Varsayılan:** `0` (gri örtü kullanılır)

### PETSINHERITNOTORIETY

Petin sahibinden hangi notoriety durumlarını devraldığı.

- **Tür:** bayraklar (`0x` öneki veya baştaki sıfır = hex)

| Bayrak | Ad | Anlamı |
|---|---|---|
| `001` | NotoGood | mavi |
| `002` | NotoGuildSame | yeşil müttefik |
| `004` | NotoNeutral | gri |
| `008` | NotoCriminal | gri suçlu |
| `010` | NotoGuildWar | turuncu |
| `020` | NotoEvil | kırmızı |
| `040` | NotoInvul | sarı |

- **Örnek:** `07a`, sahibi suçlu/savaşta/kötü/müttefik/ölümsüz ise peti de öyle gösterir; petin kendi notoriety'si ne olursa olsun.
- **Varsayılan:** `0`
- **Not:** Yalnızca `SERV.PETSINHERITNOTORIETY` ile okunur; pet notoriety'si hesaplanırken kullanılmıyor.

## GENİŞLEME PAKETİ BAYRAKLARI (EXPANSION SUPPORT)

Her bayrak genişleme paketine özel özellikleri ve istemci paketlerini açar. `0` = kapalı, hex bit maskesi = açık. Tüm `FEATURE*` anahtarları OR'lanıp login'de `0xB9` PacketFeatureEnable paketiyle istemciye gönderilir (açılışta config'ten doldurulur).

### FEATURET2A

The Second Age (1998).

- **Tür:** hex/integer — Source-X `FEATURE_T2A` yetenek maskesi (update/chat). Ham `0xB9` biti değildir; istemcinin ResDisp seviyesine göre protokol bitlerine çevrilir.
- **Varsayılan:** `0x1`
- **Not:** `0xB9` ile gönderilir.

### FEATURELBR

Lord Blackthorn's Revenge (2002).

- **Tür:** hex/integer — Source-X `FEATURE_LBR` maskesi (update/sound).
- **Varsayılan:** `0`
- **Not:** `0xB9` ile gönderilir.

### FEATUREAOS

Age of Shadows (2003).

- **Tür:** hex/integer — Source-X `FEATURE_AOS` maskesi; update-A/update-B/popup/tooltip alt özelliklerini içerir.
- **Varsayılan:** `0`
- **Not:** `0xB9` ile gönderilir; `0x02` biti (update-B) AOS savaş sistemlerini de açar.

### FEATURESE

Samurai Empire (2004).

- **Tür:** hex/integer — Source-X `FEATURE_SE` maskesi.
- **Varsayılan:** `0`
- **Not:** `0xB9` ile gönderilir; `0x02` biti SE parry formülünü açar (`COMBATPARRYINGERA 0x02`).

### FEATUREML

Mondain's Legacy (2005).

- **Tür:** hex/integer — Source-X `FEATURE_ML` maskesi.
- **Varsayılan:** `0`
- **Not:** `0xB9` ile gönderilir.

### FEATUREKR

Kingdom Reborn istemci desteği (2007).

- **Tür:** hex/integer
- **Varsayılan:** `0`
- **Not:** KR istemcisi artık kullanılmıyor.

### FEATURESA

Stygian Abyss (2009).

- **Tür:** hex/integer — Source-X `FEATURE_SA` maskesi.
- **Varsayılan:** `0`
- **Not:** `0xB9` ile gönderilir.

### FEATURETOL

Time of Legends (2015).

- **Tür:** hex/integer — Source-X `FEATURE_TOL` maskesi.
- **Varsayılan:** `0`
- **Not:** `0xB9` ile, sanal altın (virtual gold) biti dahil gönderilir.

### FEATUREEXTRA

Özel ek genişleme bitleri.

- **Tür:** hex/integer
- **Varsayılan:** `0`
- **Not:** FeatureFlags OR'una dahil.

### RACIALFLAGS

Irk yetenekleri (Source-X `RACIALF_*`).

- **Tür:** hex/integer (`0x` öneki; baştaki sıfır onaltılık DEĞİL)

| Bayrak | Yetenek |
|---|---|
| `0x0001` | İnsan: Strong Back (+60 taşıma) |
| `0x0002` | İnsan: Tough (+2 HP yenilenme) |
| `0x0004` | İnsan: Workhorse (daha çok kaynak) |
| `0x0008` | İnsan: Jack of Trades (20.0 taban) |
| `0x0010` | Elf: Night Sight |
| `0x0020` | Elf: zor izlenir |
| `0x0040` | Elf: Wisdom (+20 mana) |
| `0x0080` | Gargoyle: uçma |
| `0x0100` | Gargoyle: Berserk |
| `0x0200` | Gargoyle: Deadly Aim (20.0 taban) |
| `0x0400` | Gargoyle: Mystic Insight (30.0) |

- **Varsayılan:** `0`

### ERALIMITGEAR / ERALIMITLOOT / ERALIMITPROPS

Genişleme sınırı: hangi dönemin ekipmanı, ganimeti ve eşya özellikleri kullanılır (RESDISP numarası).

- **Tür:** integer (0-9; `9` = ToL = "en yeni")
- **Varsayılan:** `9`
- **Not:** Kendi `ERALIMIT*` değerini yazmayan bir CHARDEF bu değeri alır.

### DECIMALVARIABLES

Sayısal TAG/VAR/LOCAL/CTAG değerinin script'te okunuşu ve save'e yazılışı: `0` = Sphere hex (10 `"0A"` okunur), `1` = ondalık.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0` (Source-X `CServerConfig m_fDecimalVariables`)

### DISPLAYELEMENTALRESISTANCE

`COMBATFLAGS` ELEMENTALENGINE (`0x8`) kapalı olsa bile durum paketinde element dirençlerini göster.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`
- **Not:** Durum paketine (`0x11`) uygulanır.

### OPTIONFLAGS

Sunucu ek seçenek bayrakları (Source-X `OF_*`).

- **Tür:** hex/integer bit bayrakları (`0x` öneki gerekir)
  - `0x0000080` OF_FileCommands - script'lerde `FILE.*` komutlarını aç (`FILE.OPEN`, `FILE.READLINE`, `FILE.WRITELINE` ...); FILE script nesnesini açar
  - `0x0002000` OF_Buffs - yerel buff/debuff ikonları
  - Diğerleri (`SphereNet.Core.Enums.OptionFlags`):

| Bayrak | Ad |
|---|---|
| `0x1` | NoDClickTarget |
| `0x2` | NoSmoothSailing |
| `0x4` | ScaleDamageByDurability |
| `0x8` | CommandSysMessages |
| `0x10` | PetSlots |
| `0x20` | OsiMultiSight |
| `0x40` | ItemsAutoName |
| `0x100` | NoItemNaming |
| `0x200` | NoHouseMuteSpeech |
| `0x400` | NoContextMenuLos |
| `0x800` | MapBoundarySailing |
| `0x1000` | FloodProtection |
| `0x4000` | NoPrefix |
| `0x8000` | DyeType |
| `0x10000` | DrinkIsFood |
| `0x20000` | NoDClickTurn |
| `0x40000` | NoPaperdollTradeTitle |
| `0x80000` | NoTargetTurn |
| `0x100000` | StatAllowValueOverMax |
| `0x200000` | GuardOutsideGuardedArea |
| `0x400000` | OverweightNoDropCarriedItem |
| `0x800000` | AllowContainerInsideContainer |
| `0x1000000` | VendorStockLimit |
| `0x2000000` | EnableGuildAlignNotoriety |
| `0x4000000` | NoDClickEquip |
| `0x8000000` | PetBehaviorOwnerNeutral |
| `0x10000000` | NpcMovementOldStyle |

- **Varsayılan:** `0`
- **Not:** `OPTIONFLAGS`'i yalnızca bir kez tanımlayın; ini okuyucu son değeri alır.

## HARİTA YAPILANDIRMASI

Format: `MAP<id>=genişlik,yükseklik,sektörBoyutu,mulOkumaIndex,istemciHaritaId`

- `genişlik`/`yükseklik` - tile cinsinden harita boyutu, .mul ile eşleşmeli (8'in katı)
- `sektörBoyutu` - sektör ızgara boyutu, 2'nin kuvveti (genelde 64)
- `mulOkumaIndex` - hangi `map<N>.mul` / `statics<N>.mul` okunur
- `istemciHaritaId` - istemciye gönderilen harita id'si

Standart UO haritaları:

| Anahtar | Harita | Boyut |
|---|---|---|
| `MAP0` | Felucca | 6144x4096 |
| `MAP1` | Trammel | 6144x4096 |
| `MAP2` | Ilshenar | 2304x1600 |
| `MAP3` | Malas | 2560x2048 |
| `MAP4` | Tokuno | 1448x1448 |
| `MAP5` | Ter Mur | 1280x4096 |

### MAP0 / MAP1 / MAP2 / MAP3 / MAP4 / MAP5

Yukarıdaki formatta harita tanımları. Dünya sektör kurulumu ve harita verisi başlatması tarafından uygulanır.

7168 = T2A/Lost Lands dahil tam map0 genişliği (6144 Lost Lands'i keser; mortechUO teleport tanımları x > 6144 kullanır).

- **Varsayılan:** `MAP0=6144,4096,64,0,0` (hiç MAPn yoksa)
- **Not:** `MAP0`..`MAP255` okunur. Boş değer (`MAPn=`) haritayı kapatır; sıfır veya eksik alan varsayılanını korur; geçersiz boyut/sektör açılışta bildirilir ve varsayılan kalır.

### USEMAPDIFFS

`mapdif*`/`stadif*` yama dosyalarını haritaların üzerine uygula.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

## DOSYA YOLLARI

Mutlak yollar olduğu gibi kullanılır. Göreli yollar önce çalışma dizinine (CWD) göre aranır; orada yoksa sunucu çalıştırılabilir dosyasının dizinine göre çözülür.

### MULFILES

UO istemci veri dosyalarının yolu (`map*.mul`, `statics*.mul`, `tiledata.mul` ...).

- **Tür:** string (dizin yolu)
- **Varsayılan:** `""` (boşsa çalışma/sunucu dizininde `mul` klasörü aranır)
- **Not:** Klasör bulunamazsa sunucu başlamaz.

### SCPFILES

Sunucu script dosyalarının yolu (`.scp`). Script yükleyici bu dizini tarar.

- **Tür:** string (dizin yolu)
- **Varsayılan:** `"scripts/"`
- **Not:** Source-X manifest kökü: yükleme sırasını `spheretables.scp` belirler; orada yoruma alınmış klasör/dosyalar bilerek yüklenmez.

### SCRIPTENCODING

Script metninin kodlaması: `AUTO`, `UTF8` veya `LEGACY`. `AUTO` önce UTF-8 dener, geçersizse `SCRIPTLEGACYCODEPAGE`'e düşer.

- **Tür:** `AUTO` | `UTF8` | `LEGACY` (başka = `AUTO` + uyarı)
- **Varsayılan:** `AUTO`

### SCRIPTLEGACYCODEPAGE

`LEGACY` ve `AUTO` geri dönüşünde kullanılan Windows kod sayfası.

- **Tür:** integer (kod sayfası; eski Türkçe scriptler için `1254`)
- **Varsayılan:** `1252`

### LOG

Log çıktı dizini. Serilog dosya sink'i ve Host çökme log'u tarafından kullanılır.

- **Tür:** string (dizin yolu)
- **Varsayılan:** `"logs/"`

### WORLDSAVE

Dünya kayıt dosyalarının dizini (`sphereworld.scp`, `spheredata.scp` ...).

- **Tür:** string (dizin yolu)
- **Varsayılan:** `"save/"`

### ACCTFILES

Hesap dosyalarının dizini (`sphereaccu.scp`).

- **Tür:** string (dizin yolu)
- **Varsayılan:** `"accounts/"`

## KAYDETME AYARLARI

### SAVEPERIOD

Otomatik kaydetme aralığı (dakika).

- **Tür:** integer (dakika, `0` = otomatik kayıt yok)
- **Varsayılan:** `20` (Source-X ile aynı)

### FORCEGARBAGECOLLECT

Her yeni kayıttan önce dünya nesnelerini denetle/temizle (Source-X). `0` tam taramayı atlar. .NET GC değildir. Temizlik süresi ayrı loglanır; kayıt sayacı temizlikten sonra başlar.

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### SAVEONSHUTDOWN

Düzgün kapanışta dünyayı kaydet; son periyodik kayıttan sonraki değişiklikler planlı bir durdurmada kaybolmaz.

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### SAVESECTORSPERTICK

Source-X arka plan kaydında tick başına kaydedilen sektör sayısı.

- **Tür:** integer
- **Varsayılan:** `1`
- **Not:** Yalnızca `SERV.SAVESECTORSPERTICK` ile okunur. SphereNet arka plan kaydını snapshot + worker modeliyle yapar (`SAVEBACKGROUND`'a bakın); Source-X'in tick başına sektör kademelemesi kullanılmaz.

### SAVESTEPMAXCOMPLEXITY

Source-X: arka plan kaydının bir adımda işlediği en fazla nesne karmaşıklığı.

- **Tür:** integer
- **Varsayılan:** `500`
- **Not:** Yalnızca `SERV.SAVESTEPMAXCOMPLEXITY` ile okunur; snapshot tabanlı kayıtta adım kademelemesi yok.

### SAVEBACKGROUND

Arka plan kaydını aç.

- **Tür:** integer (Source-X'te dakika cinsinden süre; SphereNet'te 0'dan büyük her değer "açık")
  - `0` = tam senkron kayıt (dünya kaydın tamamı boyunca durur, ~1sn+)
  - `>0` = arka plan kaydı: ana döngü yalnızca değişmez bir dünya anlık görüntüsü yakalar (~0.2-0.4sn); shard/sıkıştırma/yazma düşük öncelikli bir worker thread'de koşar. Kayıtlar üst üste binmez, kapanış kaydı uçuştakini bekler, atomik temp+commit korunur.
- **Varsayılan:** `0`

### BACKUPLEVELS

Tutulacak yedek kayıt kuşağı sayısı.

- **Tür:** integer (`0` = yedek yok)
- **Varsayılan:** `10`
- **Not:** `save_1/`, `save_2/` ... oluşturur.

### SAVEFORMAT

Kayıt dosya formatı.

- **Tür:** `Text` | `TextGz` | `Binary` | `BinaryGz` (veya 0-3)
  - `Text` = klasik Sphere .scp metni (insan okunabilir, büyük)
  - `TextGz` = `.scp.gz` — aynı metin, gzip sarılmış (~%85 daha küçük)
  - `Binary` = `.sbin` — tag-stream binary (aynı (key,value) semantiği, şema yok)
  - `BinaryGz` = `.sbin.gz` — binary + gzip (en küçük)
- **Varsayılan:** `Text` (bilinmeyen değer = `Text`)
- **Not:** Yükleyici formatı dosya uzantısından algılar; değiştirmek eski snapshot'ları bozmaz. Çalışırken `.SAVEFORMAT` komutuyla da değişir.

### SAVESHARDS

Shard modu.

- **Tür:** integer (0-16)
  - `0` = her zaman tek dosya, rolling kapalı (klasik Sphere)
  - `1` = boyuta göre rolling: `SAVESHARDSIZEMB`'e göre 1, 2, 3... dosya
  - `2-16` = sabit paralel hash shard (N dosya, her biri kendi Task'inde yazılır)
- **Varsayılan:** `3`

### SAVESHARDSIZEMB

Rolling modunda dosya başına en fazla boyut (MB).

- **Tür:** integer (MB)
- **Varsayılan:** `75` (BinaryGz: shard başına ~4-6M item veya ~900K-1.8M NPC)
- **Not:** Yalnızca `SAVESHARDS=1` iken. Tek dosyayla başlar, limiti aşınca `{base}.1.{ext}`, `{base}.2.{ext}` açar; küçük dünyalar tek dosyada kalır (klasik Sphere davranışı).

## DÜNYA ZAMANLAMASI

### GAMEMINUTELENGTH

Oyun içi bir dakikanın gerçek dünya karşılığı (saniye). Dünya saatini (gece/gündüz) yürütür.

- **Tür:** integer (saniye, en az 1)
- **Varsayılan:** `20` (Source-X ile aynı)
- **Not:** 8'de tam bir oyun günü (24s) = 8*60*24 = 11520 sn ≈ 3.2 gerçek saat.

### SERVERTICKMS

Dünya döngüsü tick aralığı (milisaniye).

- **Tür:** integer (ms, 20-250 arasına kırpılır)
- **Varsayılan:** `100` (saniyede 10 tick, Source-X gibi)
- **Not:** Ağ G/Ç'si buna bağlı değildir; her ana döngü turunda çalışır. `<TICKPERIOD>` okuyan script saniyedeki tick sayısını (`1000/SERVERTICKMS`) alır.

### TICKPERIOD

Saniyedeki sunucu tick sayısı; Source-X bu birimde döndürür (`<SERV.TICKPERIOD>` = 10). Yalnızca SERVERTICKMS yoksa kullanılır, bu yüzden ini'de yorumda: 10, 100 ms'lik tick demektir.

- **Tür:** integer (saniyedeki tick)
- **Varsayılan:** `10`
- **Not:** Yalnızca SERVERTICKMS yoksa etkilidir.

### SECTORSLEEP

Bir sektörün, içindeki son oyuncu ayrıldıktan sonra uykuya geçmeden önce tick'lemeye devam ettiği süre (dakika).

- **Tür:** integer (dakika, `0` = asla uyuma)
- **Varsayılan:** `10` (Source-X ile aynı); bu paket `1` kullanır
- **Not:** Uyuyan sektörde NPC AI ve yenilenme işlemez; item TIMER'ları 3 dakikalık bakım taramasına düşer (çürüme ayrı bir 5 sn'lik yakalama geçişiyle yürür). Oyuncunun içinde bulunduğu sektör bu süreyle işaretlenir, 5x5 penceresi değil: gezginin arkasında bıraktığı iz bir sektör genişliğindedir.
- **Not:** `0`, Source-X'teki anlamıyla aynı DEĞİLDİR. Source-X her sektörü tick'ler ve kararı `_CanSleep`'e bırakır; burada tick kümesi 5x5 penceresinden kurulur ve yalnızca geçen tick uyanık olan sektörlere sorulur. Yani `0` şu demektir: bir kez uyanan sektör bir daha uyumaz, hiç yaklaşılmamış sektör ise yine hiç tick'lemez. Uyanık küme yalnızca büyür (ölçüm: bir oyuncu sekiz alan gezerken 32 -> 137 sektör) ve büyümeye devam eder. "Neredeyse hiç uyumasın" için `0` değil küçük bir değer kullanın.

### TIMERCALL

Sunucu "timer" kancasının (Source-X `f_onserver_timer`) çağrılma aralığı.

- **Tür:** integer (dakika; `TIMERCALLUNIT=1` ise saniye)
- **Varsayılan:** `0` = kapalı
- **Not:** Aralık (kendi biriminde) `ARGN1` olarak verilir (Source-X CWorld.cpp:1831).

### TIMERCALLUNIT

TIMERCALL'un birimi.

- `0` = dakika
- `1` = saniye

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

### MAPCACHETIME

Harita veri bloklarının bellekte önbellekte kalma süresi (saniye).

- **Tür:** integer (saniye, `0` = önbellek yok)
- **Varsayılan:** `120`
- **Not:** Uygulanmadı: anahtar SphereConfig'de tanımlı değil, bu yüzden etkisi yoktur.

### ZEROPOINT

Sekstant koordinatlarının sıfır noktası (Lord British'in tahtı). Sekstant/harita koordinatlarında kullanılır.

- **Tür:** string `"x,y[,z[,m]]"`
- **Varsayılan:** `1323,1624,0`
- **Not:** Hatalı bir değer mevcut sıfır noktasını korur.

## GÖRÜŞ ALANI & MESAFE

### MAPVIEWSIZE

Varsayılan istemci görüş mesafesi (tile yarıçapı). Bunun ötesindeki nesneler istemciye gönderilmez. İstemci `0xC8` ile kendi mesafesini isteyebilir (MAPVIEWSIZEMAX'a kadar). `MAPVIEWRADAR=0` iken savaş mesafesi olarak da kullanılır.

- **Tür:** integer (tile, 1-255 arasına kırpılır)
- **Varsayılan:** `18`

### MAPVIEWSIZEMAX

İstemcinin `0xBF.0x1C` / `0xC8` ile isteyebileceği en büyük görüş mesafesi.

- **Tür:** integer (5-255 arasına kırpılır)
- **Varsayılan:** `24` (Source-X ile aynı)

### MAPVIEWRADAR

Savaş hafızasının ve NPC takibinin geçerli olduğu mesafe (tile); bundan uzaklaşan hedef savaştan düşer.

- **Tür:** integer (tile)
- **Varsayılan:** `31` (UO_MAP_VIEW_RADAR, Source-X gibi); `0` = MAPVIEWSIZE
- **Not:** `0`, MAPVIEWSIZE demektir (SphereNet eki).

### DISTANCEWHISPER / DISTANCETALK / DISTANCEYELL

Fısıltı, normal konuşma ve bağırmanın duyulduğu mesafe (tile).

- **Tür:** integer (tile)
- **Varsayılan:** `3` / `18` / `31` (Source-X ile aynı)

### MAXCOMPLEXITY

Bir sektörün "fazla karmaşık" sayılmadan tutabileceği karakter sayısı.

- **Tür:** integer
- **Varsayılan:** `32`
- **Not:** Aşılınca konsola uyarı düşer; yarısı aşılan sektörde NPC'ler etrafa bakma (look-around) taramasını atlar.

### MAXSECTORCOMPLEXITY

Bir sektörün uyarı vermeden ("Sector too complex!") tutabileceği yerdeki eşya sayısı.

- **Tür:** integer
- **Varsayılan:** `1024`

## STAT & YETENEK AYARLARI

### MAXBASESKILL

Source-X'in YENİ karakterin her yeteneğine, seçilen yetenekler ayarlanmadan önce verdiği rastgele taban değerin üst sınırı (`200` = 0-20.0).

- **Tür:** integer (onda bir)
- **Varsayılan:** `200`
- **Not:** Bu bir yetenek tavanı DEĞİLDİR. Toplam tavan karakterin SKILLCLASS SKILLSUMMAX değerinden gelir. SphereNet rastgele başlangıç yeteneği vermez.
- **Not:** Yalnızca okunur: değer yalnızca `SERV.MAXBASESKILL` ile okunur.

### MAXPOLYSTATS

Polymorph'un bir statı en fazla kaç puan oynatabileceği. Formun kendi STR/DEX'i alınır ama iki yönde de bu kadarla sınırlıdır (Source-X CCharSpell.cpp:1082). Yalnızca MAGICFLAGS POLYMORPHSTATS açıkken okunur.

- **Tür:** integer (puan, 0 veya üstü)
- **Varsayılan:** `150`

### MAXFAME

Bir karakterin sahip olabileceği en yüksek şöhret (fame) (fame değişim tavanı).

- **Tür:** integer
- **Varsayılan:** `10000`

### MAXKARMA

En yüksek (en iyi) karma.

- **Tür:** integer
- **Varsayılan:** `10000`

### MINKARMA

En düşük (en kötü) karma.

- **Tür:** integer
- **Varsayılan:** `-10000`

### STATSFLAGS

Stat davranışını kontrol eden bit bayrakları (Source-X CServerConfig.h:496, CCharStat.cpp:233).

| Değer | Anlamı |
|---|---|
| `01` | MAXHITS/MAXMANA/MAXSTAM yazımı herkes için reddedilir |
| `02` | ... yalnızca oyuncular için |
| `04` | ... yalnızca NPC'ler için |

- **Tür:** bayrak (Sphere kuralı)
- **Varsayılan:** `0`
- **Not:** Reddedilen yazım havuzu taban stata (STR/INT/DEX) döndürür.

### EXPERIENCESYSTEM

Source-X deneyim (EXP) sistemi. Kapalıyken savaş ve craft deneyimi hiç verilmez.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

### EXPERIENCEMODE

Deneyim kazanım kuralları (Source-X `EXP_MODE_*`). `0` deneyimi tamamen dondurur.

| Değer | Ad | Anlamı |
|---|---|---|
| `01` | RAISE_COMBAT | savaşta deneyim kazan |
| `02` | RAISE_CRAFT | craft'ta deneyim kazan |
| `04` | ALLOW_DOWN | deneyim azalabilir |
| `08` | DOWN_NOLEVEL | azalma mevcut seviyenin altına inmez |
| `010` | AUTOSET_EXP | @Create'te EXP/LEVEL yazmayan NPC'lere otomatik ver |

- **Tür:** bayrak (Sphere kuralı)
- **Varsayılan:** `0`

### EXPERIENCEKOEFPVP / EXPERIENCEKOEFPVM

Oyuncu-oyuncu ve oyuncu-canavar öldürmelerinden kazanılan deneyimin yüzdesi (ölüm deneyimi hesabı).

- **Tür:** integer (%)
- **Varsayılan:** `100`

### LEVELSYSTEM

Deneyimden seviye (LEVEL) hesabı.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

### LEVELMODE

- `0` = doğrusal (her LEVELNEXTAT deneyim bir seviye)
- `1` = katlanan (`LEVELNEXTAT * (seviye+1)` gerekir)

- **Tür:** integer (0/1)
- **Varsayılan:** `1`

### LEVELNEXTAT

Bir sonraki seviye için gereken deneyim (LEVELMODE'a bakın).

- **Tür:** integer (0 veya üstü)
- **Varsayılan:** `0` (seviye hesaplanmaz)

## REJENERASYON ORANLARI

SANİYE cinsinden: bir puanın yenilenmesi için geçen süre (Source-X ile aynı birim ve sıra: REGEN0=STR/HP, REGEN1=INT/mana, REGEN2=DEX/stamina, REGEN3=yemek). `REGEN0=40` = her 40 saniyede bir HP. Karakterin kendi REGENHITS/REGENMANA/... değeri bunları ezer; açlık ve meditasyon da etkiler.

### REGEN0

Can puanı (HP) yenilenme aralığı (saniye).

- **Tür:** integer (saniye)
- **Varsayılan:** `40`

### REGEN1

Mana yenilenme aralığı (saniye). Meditation hızlandırır.

- **Tür:** integer (saniye)
- **Varsayılan:** `20`

### REGEN2

Dayanıklılık (stamina) yenilenme aralığı (saniye).

- **Tür:** integer (saniye)
- **Varsayılan:** `10`

### REGEN3

Açlık: yemek değerinin bir puan düşmesi için geçen süre (saniye).

- **Tür:** integer (saniye)
- **Varsayılan:** `3600` (= 1 saat)
- **Not:** Source-X referans ini'si gibi çarpım olarak yazılabilir (`60*60*24`).

## SAVAŞ AYARLARI

### COMBATFLAGS

Savaş davranışını kontrol eden bit bayrakları (Source-X `COMBAT_*`).

| Değer | Ad | Anlamı |
|---|---|---|
| `0x00001` | NODIRCHANGE | saldırırken yön değiştirme |
| `0x00002` | FACECOMBAT | hedefe dön |
| `0x00004` | PREHIT | ilk vuruş gecikmesiz (SWING_NORANGE ile olmaz) |
| `0x00008` | ELEMENTALENGINE | element sistemi |
| `0x00020` | DCLICKSELF_UNMOUNTS | savaşta kendine çift tık binekten indirir |
| `0x00040` | ALLOWHITFROMSHIP | gemiden vurulabilir |
| `0x00080` | NOPETDESERT | pet terk etmez |
| `0x00100` | ARCHERYCANMOVE | okçu yürürken atar |
| `0x00200` | STAYINRANGE | menzilde kal |
| `0x01000` | STACKARMOR | zırhlar birikir |
| `0x02000` | NOPOISONHIT | zehirli silah vuruşta zehirlemez |
| `0x04000` | SLAYER | slayer sistemi |
| `0x08000` | SWING_NORANGE | menzil dışında biter |
| `0x10000` | ANIM_HIT_SMOOTH | vuruş animasyonu salınıma yayılır |
| `0x20000` | FIRSTHIT_INSTANT | ilk vuruş anında |
| `0x40000` | NPC_BONUSDAMAGE | NPC'ler de bonus alır |
| `0x80000` | PARALYZE_CANSWING | felçliyken saldırı sürer |
| `0x100000` | ATTACK_NOAGGREIVED | katile saldıran suçlu olmaz |

- **Tür:** hex/integer (`0x` öneki gerekir; Source-X usulü `040000` burada ONDALIK okunur)
- **Varsayılan:** `0`
- **Not:** PREHIT ve SWING_NORANGE birlikte olamaz; SWING_NORANGE açılışta uyarıyla kapatılır.

### COMBATDAMAGEERA

Hasar formülü dönemi (Source-X ile aynı değerler).

- `0` = Sphere özel
- `1` = pre-AOS
- `2` = AOS

- **Tür:** integer (0-2)
- **Varsayılan:** `0`

### COMBATHITCHANCEERA

İsabet şansı formülü dönemi (Source-X ile aynı değerler).

- `0` = Sphere özel (Hit/Defense Chance Increase ile ölçeklenir; uyuyan veya donmuş hedefe en az %80 isabet)
- `1` = pre-AOS (HCI ile uyumsuz)
- `2` = AOS

- **Tür:** integer (0-2)
- **Varsayılan:** `0`

### COMBATHITCHANCELEGACYERA0

SphereNet anahtarı. 0.56 / eski Source-X dönem-0 isabet zarını geri getirir: şans rastgele bir çekiliştir `rand(şans)`, uyuyan/donmuş hedef `rand(10)` çeker, HCI/DCI yok sayılır. `0` = güncel Source-X formülü (hesaplanan şans kullanılır).

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

### COMBATSPEEDERA

Salınım hızı formülü dönemi (Source-X ile aynı değerler).

- `0` = Sphere özel (DEX kullanır; önerilen SPEEDSCALEFACTOR 15000)
- `1` = pre-AOS (STAM kullanır; önerilen 15000)
- `2` = AOS (Swing Speed Increase; önerilen 40000)
- `3` = SE (önerilen 80000)
- `4` = ML (SPEEDSCALEFACTOR kullanmaz)

- **Tür:** integer (0-4)
- **Varsayılan:** `0`

### COMBATPARRYINGERA

Parry davranışı bayrakları (Source-X `PARRYERA_*`).

| Değer | Ad | Anlamı |
|---|---|---|
| `0x01` | PRESEFORMULA | pre-SE parry şansı (Bushido'suz); 0x02 ile birlikte olmaz |
| `0x02` | SEFORMULA | SE formülü (Bushido da sayılır; FEATURESE 0x02 gerekir) |
| `0x10` | SHIELDBLOCK | kalkanla parry |
| `0x20` | ONEHANDBLOCK | kalkansız tek elli silahla parry |
| `0x40` | TWOHANDBLOCK | iki elli silahla parry |
| `0x80` | ARSCALING | kalkan AR'si Parrying ile ölçeklenir |

- **Tür:** bayrak, Sphere kuralı (baştaki sıfır = onaltılık, `|` değerleri OR'lar, örn. `01|010`)
- **Varsayılan:** `01|010` (PRESEFORMULA | SHIELDBLOCK, Source-X varsayılanı)
- **Not:** DİKKAT: `0` hiçbir parry yolunu açmaz — kimse parry yapamaz.

### COMBATARCHERYMOVEMENTDELAY

Hareketten sonra okçunun atış yapabilmesi için beklenen süre, SANİYENİN ONDA BİRİ (`10` = 1 sn), Source-X gibi (içeride ms = değer * 100).

- **Tür:** integer (saniyenin onda biri; `0` = gecikme yok)
- **Varsayılan:** `10` (Source-X; SE 0.25 sn / AOS 0.5 sn / pre-AOS 1.0 sn)
- **Not:** COMBATFLAGS ARCHERYCANMOVE (`0x100`) veya karakterin ARCHERCANMOVE bayrağı gecikmeyi kaldırır.

### COMBATMELEEMOVEMENTDELAY

Hareketten sonra yakın dövüş salınımının başlayabilmesi için beklenen süre (ms). SphereNet'e özgüdür; Source-X'te karşılığı yoktur.

- **Tür:** integer (ms, `0` = gecikme yok)
- **Varsayılan:** `0`

### ATTACKERTIMEOUT

Hareketsiz kalan savaş hafızasının (saldırgan listesi) silinme süresi.

- **Tür:** integer (saniye, `0` = asla)
- **Varsayılan:** `30` (Source-X varsayılanı; referans ini'si 300)

### ARCHERYMINDIST

Okçuluk saldırıları için en kısa menzil (tile); silahın kendi RANGE'i aksini söylemiyorsa.

- **Tür:** integer (tile)
- **Varsayılan:** `2` (Source-X ile aynı)

### ARCHERYMAXDIST

Okçuluk saldırıları için en uzak menzil (tile); silahın kendi RANGE'i aksini söylemiyorsa.

- **Tür:** integer (tile)
- **Varsayılan:** `15` (Source-X ile aynı)

### SPELLTIMEOUT

Oyuncunun bir büyüye hedef seçmesi için tanınan süre (saniye). Süresi dolan imleç iptal edilir ve istemciye de kapattırılır. Zaman aşımsız bir imleç zararsız değildir: oturumun kalanı boyunca açık kalır ve oyuncunun tıkladığı bir sonraki şey — bir kapı, bir arkadaş — dakikalar önce yapılan büyüye cevap verir. Karakterin kendi `TAG.SPELLTIMEOUT`'u bunu ezer (CClientUse.cpp:1061).

- **Tür:** integer (saniye)
- **Varsayılan:** `0` = zaman aşımı yok

### MAGICUNLOCKDOOR

BÜYÜYLE kilitli bir kapının maymuncukla açılma zorluğu. Referans ini "gereken lockpicking yeteneği" der, ama kod bunu yetenek hiç sorulmadan önce atılan N'de-bir ŞANS olarak kullanır (CItem.cpp:5428). Sıradan kilitleri etkilemez.

- `0` = böyle bir kapı anahtarını ister, maymuncukla açılmaz
- `-1` = özel kural yok; büyülü kapı sıradan kilit gibi açılır

- **Tür:** integer
- **Varsayılan:** `900`

### MEDITATIONMOVEMENTABORT

Adım atmak meditasyonu iptal eder mi? Şaşırtıcı olan yarısı varsayılandır: referans meditasyon yapan karakterin YÜRÜMESİNE İZİN VERİR; yalnızca bu anahtar kuruluyken yetenek başarısız olur (CCharAct.cpp:2495). SphereNet 2026-09-14 öncesinde koşulsuz iptal ediyordu — referanstan daha katı.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

### NORESROBE

Dirilen oyuncuya cüppe VERME. Hayaletin kefeninden AYRI bir ayardır (CCharSpell.cpp:503); ikisini bağlamak, görünür hayalet isteyen bir shard'ın insanları çıplak diriltmesi demek olurdu.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0` (cüppe verilir)

### HITSUPDATERATE

Diğer istemcilere HP çubuğu güncelleme aralığı (saniyenin onda biri).

- **Tür:** integer
- **Varsayılan:** `30` (= 3.0 sn)
- **Not:** Uygulanmadı: anahtar SphereConfig'de tanımlı değil, bu yüzden etkisi yoktur.

### SPEEDSCALEFACTOR

Salınım hızı formülünün böleni (COMBATSPEEDERA'ya bakın).

- **Tür:** integer (en az 1)
- **Varsayılan:** `15000`

## BÜYÜ AYARLARI

### MAGICFLAGS

Büyü davranışını kontrol eden bit bayrakları.

`0x1`-`0x10000` bitleri Source-X `MAGICF_*`'dir (SphereNet.Core.Enums.MagicConfigFlags):

| Değer | Ad |
|---|---|
| `0x0000001` | NODIRCHANGE |
| `0x0000002` | PRECAST |
| `0x0000004` | IGNOREAR |
| `0x0000008` | CANHARMSELF |
| `0x0000010` | STACKSTATS |
| `0x0000020` | FREEZEONCAST |
| `0x0000040` | SUMMONWALKCHECK |
| `0x0000080` | NOFIELDSOVERWALLS |
| `0x0000100` | NOANIM |
| `0x0000200` | OSIFORMULAS |
| `0x0000400` | NOCASTFROZENHANDS |
| `0x0000800` | POLYMORPHSTATS |
| `0x0001000` | OVERRIDEFIELDS |
| `0x0002000` | CASTPARALYZED |
| `0x0004000` | NOREFLECTOWN |
| `0x0008000` | DELREFLECTOWN |
| `0x0010000` | NOINTERRUPT |

SphereNet ekleri:

| Değer | Anlamı |
|---|---|
| `0x0020000` | hedefe LOS gerekmez |
| `0x0040000` | ölümde polymorph geri döner |
| `0x0080000` | açık alan büyüleri zindanda çalışır |
| `0x0100000` | gate kapalı |
| `0x0200000` | recall kapalı |
| `0x0400000` | mark kapalı |
| `0x0800000` | summon sınırı |
| `0x1000000` | gate iki yönlü açılır |
| `0x2000000` | cast'te reveal yok |
| `0x4000000` | dispel çağrılmışı öldürür |

- **Tür:** hex/integer (`0x` öneki gerekir)
- **Varsayılan:** `0`

### REAGENTSREQUIRED

Büyü yapmak için reagent (malzeme) gerekir.

- `0` = reagent gerekmez (ücretsiz büyü)
- `1` = cast'te harcanır (standart UO)

- **Tür:** boolean (0/1)
- **Varsayılan:** `0` (Source-X ile aynı)
- **Not:** Wand, scroll ve GM+ muaf.

### SPELLBOOKREQUIRED

Büyü yapmak için büyünün yazılı olduğu büyü kitabı gerekir.

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### EQUIPPEDCAST

Silah kuşanmışken büyü yapmaya izin ver.

- `0` = silah çıkarılmalı (klasik UO)
- `1` = silahla büyü yapılabilir

- **Tür:** boolean (0/1)
- **Varsayılan:** `1` (Source-X ile aynı)

### REAGENTLOSSABORT

Cast iptal edilince reagent'lar kaybedilsin.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

### REAGENTLOSSFAIL

Fizzle'da reagent'lar kaybedilsin.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

### MANALOSSABORT

Cast iptal edilince mana kaybedilsin.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

### MANALOSSFAIL

Fizzle'da mana kaybedilsin.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

### MANALOSSPERCENT

Büyünün mana maliyetinin gerçekte harcanan yüzdesi (`50` = yarı maliyet, `0` = ücretsiz).

- **Tür:** integer (%)
- **Varsayılan:** `50` (Source-X ile aynı)

### WOPPLAYER / WOPSTAFF

Oyuncular / görevliler (Counselor+) büyü yaparken güç sözlerini (Words of Power) söylesin.

- **Tür:** boolean (0/1)
- **Varsayılan:** `WOPPLAYER=1`, `WOPSTAFF=0`

### WOPCOLOR / WOPFONT / WOPTALKMODE

- **WOPCOLOR** - Güç sözlerinin rengi (`0` = konuşanın kendi rengi).
- **WOPFONT** - Güç sözlerinin fontu (`0` = varsayılan font 3).
- **WOPTALKMODE** - Güç sözlerinin konuşma tipi (`10` = TALKMODE_SPELL; 1-14 dışı 10 olur).

- **Tür:** integer (`0x` öneki veya baştaki sıfır = hex)
- **Varsayılan:** `WOPCOLOR=0x03B2`, `WOPFONT=3`, `WOPTALKMODE=10`
- **Not:** Büyünün kendi @SpellCast yerelleri (`WOPColor`/`WOPFont`) bunları ezer.

## NPC AYARLARI

### NPCAI

Tüm NPC'lere uygulanan AI bayrakları (Source-X `NPC_AI_*`); karakterin kendi `OVERRIDE.NPCAI`'si bunu ezer.

| Bayrak | Ad | Anlamı |
|---|---|---|
| `0x0001` | PATH | yol bulma (A*) |
| `0x0002` | FOOD | NPC'ler yiyecek arar |
| `0x0004` | EXTRA | ek davranışlar |
| `0x0008` | ALWAYSINT | her NPC zeki sayılır |
| `0x0010` | INTFOOD | zeki NPC'ler yiyecek arar |
| `0x0040` | COMBAT | gelişmiş savaş kararları |
| `0x0080` | VENDTIME | satıcılar mesai saatine uyar |
| `0x0100` | LOOTING | canavarlar ganimet toplar |
| `0x0200` | MOVEOBSTACLES | engelleri iter |
| `0x0400` | PERSISTENTPATH | bulunan yolu saklar |
| `0x0800` | THREAT | tehdit listesiyle hedef seçer |

- **Tür:** hex/tamsayı (`0x` öneki veya baştaki sıfır = hex)
- **Varsayılan:** Anahtar hiç yazılmazsa `0x0C41` (PATH | COMBAT | PERSISTENTPATH | THREAT); çıplak Source-X varsayılanı `0`.
- **Not:** DİKKAT: bu paket `0` yazar; yol bulma ve tehdit takibi dahil hepsi kapalıdır (çıplak Source-X davranışı).

### NPCAIEXTRAS

Source-X'te OLMAYAN isteğe bağlı NPC davranışları. `0` = hiçbiri: NPC'ler Source-X gibi davranır. İstediğiniz bitleri ekleyin (`|` ile).

| Bayrak | Ad | Anlamı |
|---|---|---|
| `01` | SMARTCASTER | büyüler role göre seçilir (iyileştirme, lanet, alan, hasar, summon); büyü listesinden ve spell flag'lerinden |
| `02` | LOWHPRETREAT | ağır yaralı yakın dövüşçü geri çekilir |
| `04` | ALLYRALLY | yakındaki aynı türden NPC'ler kavgaya katılır |
| `08` | SURROUNDFLANK | saldıranlar hedefin etrafına dağılır |
| `010` | FLEETACTICS | kaçan NPC büyü yapar, iyileşir ya da saklanır |
| `020` | HIDDENPURSUIT | saklanan hedefin peşine düşülür ve açığa çıkarılır |
| `040` | LOSRECOVERY | görüş yoksa görünen başka düşmana geç ya da ışınlan |
| `080` | DETECTHIDDEN | guard'lar ve Detect Hidden'lı NPC'ler saklananları arar |
| `0100` | ANIMALBACKOFF | ürkek hayvanlar savaş modundakilerden uzaklaşır |
| `0200` | FIGHTRESCAN | dövüşen NPC arada hedeflerini yeniden değerlendirir |
| `0400` | HURTSLOWDOWN | ağır yaralı NPC yavaşlar |
| `0800` | PETKEEPPACE | takip eden pet koşan ya da binekli sahibine yetişir |
| `01000` | RETURNHOME | evinden uzaklaşan NPC, sektörü uyuyunca ya da eve dönemeyince (`@NPCLostTeleport` ile) eve gönderilir |
| `02000` | BANDAGEHEAL | Healing'i ve bandajı olan NPC kendini (pet ise sahibini de) sarar |
| `04000` | IDLEFLAVOR | süs amaçlı boşta animasyonlar |
| `08000` | COMBATEXTRAS | Source-X'in ötesindeki diğer savaş eklemeleri |

- **Tür:** bayrak, Sphere kuralı (baştaki sıfır = onaltılık, `|` değerleri OR'lar)
- **Varsayılan:** `0`
- **Not:** Karakterdeki `TAG.OVERRIDE.NPCAIEXT` o NPC için bu maskenin yerine geçer.

### NPCHEALTHRESHOLD

NPC'nin kendini iyileştirmeye karar verdiği can yüzdesi; `@NPCActCast`'e `LOCAL.HealThreshold` olarak da verilir.

- **Tür:** tamsayı (%, `0` = 30)
- **Varsayılan:** `30`

### NPCWANDERLOOKAROUNDCHANCE

Boşta dolaşan NPC'nin bir adımda etrafı tarama şansı.

- **Tür:** tamsayı (%)
- **Varsayılan:** `30` (Source-X ile aynı)
- **Not:** Source-X değeri 0-99 zarından önce en az 100'e çıkarır (CCharNPCAct.cpp:1247), yani bu tarama orada da hiç çalışmaz; buradaki dolaşma AI'si bununla aynıdır.
- **Not:** Yalnızca `SERV.NPCWANDERLOOKAROUNDCHANCE` ile okunur.

### NPCTRAINCOST

NPC'den eğitim alırken 1.0 yetenek puanı başına altın.

- **Tür:** tamsayı (altın)
- **Varsayılan:** `30`
- **Not:** Yalnızca `SERV.NPCTRAINCOST` ile okunur; eğitim fiyatı `TRAINSKILLCOST`'tan gelir.

### TRAINSKILLCOST

NPC eğitiminde 0.1 yetenek puanı başına altın.

- **Tür:** tamsayı (altın, 0 veya üstü)
- **Varsayılan:** `1`

### NPCTRAINMAX / TRAINSKILLMAX

NPC'nin bir oyuncuyu eğitebileceği en yüksek yetenek (onda birlik).

- **Tür:** tamsayı (0 veya üstü)
- **Varsayılan:** `420` (= 42.0)
- **Not:** Eski SphereNet adı `TRAINSKILLMAX` da kabul edilir; ikisi varsa bu kazanır. Eğitmenin `OVERRIDE.TRAINSKILLMAX` tag'i bunu ezer.

### REVEALFLAGS

Hangi eylemlerin gizliliği bozduğu.

| Bayrak | Ad | Anlamı |
|---|---|---|
| `0001` | DETECTINGHIDDEN | Detecting Hidden gerçekten açığa çıkarsın |
| `0002` | LOOTINGSELF | kendi cesedini yağmalarken açığa çık |
| `0004` | LOOTINGOTHERS | başkasının cesedini yağmalarken açığa çık |
| `0008` | SPEAK | konuşurken açığa çık (`TAG.OVERRIDE.NOREVEALSPEAK` muaf) |
| `0010` | SPELLCAST | büyü başlarken açığa çık |
| `0020` | OSILIKEPERSONALSPACE | TERS: birisi üstüne bastığında açığa ÇIKMA |
| `0040` | SNOOPING | TERS: gözetlemeye başlarken açığa ÇIKMA |
| `0080` | STEALING | TERS: çalmaya başlarken açığa ÇIKMA |
| `0100` | STEALING_SUCCESS | hırsızlık BAŞARIRSA açığa çık |
| `0200` | STEALING_FAIL | hırsızlık BAŞARISIZ olursa açığa çık |
| `0400` | ONHORSE | at üstünde sinsi adım açığa çıkarır |

- **Tür:** bayraklar (`|` ile birleşir; BAŞTAKİ SIFIR ONALTILIKTIR: `010` = 16)
- **Varsayılan:** `01|02|04|08|010|040|080|0200` (referans ini'nin gönderdiği)
- **Not:** DİKKAT: üç bayrak (`0020`/`0040`/`0080`) adının TERSİ yönde çalışır; hepsini aynı yönde okumak bir shard'ın gizlilik kurallarının yarısını ters çevirir.

### HITSHUNGERLOSS

Açlıktan ölen karakterin her yemek tick'inde kaybettiği can.

- **Tür:** tamsayı (DÜZ miktar)
- **Varsayılan:** `0` = kapalı
- **Not:** Referans ini "% of hits" der, kod düz miktar verir (DAMAGE_FIXED, CCharAct.cpp:5788) — kod esas alındı. Referans ini bu satırı yorumda gönderir. Görevliler, uyuyanlar ve ölüler bedelsiz aç kalır.

### SKILLPRACTICEMAX

Talim mankeni / okçuluk hedefiyle çıkılabilecek en yüksek yetenek.

- **Tür:** tamsayı (onda birlik: `300` = 30.0)
- **Varsayılan:** `300`

### WOOLGROWTHTIME

Kırkılan koyunun yününün geri uzama süresi (DAKİKA).

- **Tür:** tamsayı (dakika)
- **Varsayılan:** `30`

### OVERSKILLMULTIPLY

Oyuncunun yetenek/statlarının sınıf sınırını kaç katına kadar aşmasına göz yumulacağı; aşarsa GİRİŞTE sınıra çekilir. Tavan değil ONARIM geçişidir: sınır zaten kazanım yolunda uygulanıyor; bu elle düzenlenmiş kaydı, aşan bir script'i veya canlı shard'da düşürülmüş bir sınıf sınırını yakalar (CChar.cpp:997). Görevliler muaftır.

- **Tür:** tamsayı (çarpan; 0 veya altı = kapalı)
- **Varsayılan:** `2`

### NPCTRAINPERCENT / TRAINSKILLPERCENT

NPC'nin kendi yeteneğinin öğretebileceği yüzdesi.

- **Tür:** tamsayı (0-100)
- **Varsayılan:** `30`
- **Not:** Eski SphereNet adı `TRAINSKILLPERCENT` de kabul edilir; ikisi varsa bu kazanır.

### NPCDISTANCEHEAR

NPC'nin konuşmayı "duyabildiği" mesafe (tile).

- **Tür:** tamsayı (tile)
- **Varsayılan:** `14` (Source-X `UO_MAP_VIEW_SIGHT`)
- **Not:** Negatif değer mutlak değerini yarıçap alır ve görüş şartını kaldırır.

### NPCHEARTHROUGHWALLS

Göremediği halde de duyulan kelimeler (virgülle). Boş = üst kaynağın davranışı: görüş sınavı her kelimeye uygulanır. Varsayılan "bank": bina içindeki bankacı sokaktan seslenildiğinde yanıt verir — bir oyuncunun bankadan beklediği budur — ama "buy" ve diğerleri duvarın arkasında kalır, böylece bir sokak dolusu dükkancı iki kişinin sohbetine cevap vermez. Üst kaynaktan bilinçli bir sapmadır.

Konuşma işlemedeki NPC kelime görüş sınavına uygulanır.

- **Tür:** metin (virgülle kelimeler)
- **Varsayılan:** `bank`

### PAYFROMPACKONLY

Bir karakterin ödeme yaparken parasını nereden aldığı.

- `0` = Karakterin TAŞIDIĞI her yerden; banka kutusu da giyili olduğu için bankadaki altın harcanabilir ve durum çubuğunda görünür. Çanta önce harcanır, yani bozuk para bankaya uzanılmadan önce gider.
- `1` = Yalnızca çanta; bankadaki altın harcanamaz.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0` (üst kaynakla aynı)

### NPCSKILLSAVE

Dünya OKUNURKEN bu değerin altındaki NPC yetenekleri düşürülür; adının aksine bir kayıt anahtarı değildir (Source-X FixWeirdness, CChar.cpp:1030). Oyuncu yeteneklerine dokunulmaz; `0` her şeyi korur.

- **Tür:** tamsayı (onda birlik: `100` = 10.0)
- **Varsayılan:** `10` (= 1.0)
- **Not:** Bu paket `100` kullanır: 10.0 altındaki NPC yetenekleri düşer.

### MOVERATE

Tüm NPC'lerin varsayılan hareket hızı, yüzde olarak. YÜKSEK OLAN YAVAŞTIR: tick aralığı bu değerle çarpılır (CCharNPCAct.cpp:639). CHARDEF'in kendi `MOVERATE`'i bunu ezer; bu, yazmayan tanımların başladığı değerdir (CCharBase.cpp:37). Her yaratığı yavaşlatmak için tek yer.

- **Tür:** tamsayı (%)
- **Varsayılan:** `100`

### MONSTERFEAR

Canavarlar düşük canda kaçar.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0` (Source-X ile aynı)

### MONSTERFIGHT

Canavarlar birbirleriyle (başka türden NPC'lerle) dövüşür. Kapalıyken berserk olmayan bir NPC oyuncu olmayan hedef seçmez.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

### NPCCANFIZZLEONHIT

NPC'nin büyüsü de hasar alınca bozulabilsin mi? Kapalıyken yalnızca oyuncuların büyüsü bozulur (Source-X CCharFight.cpp:881).

- **Tür:** boolean (0/1)
- **Varsayılan:** `1` (Source-X: `0`)

### NPCNOFAMETITLE

NPC'ler yüksek fame'de "Lord/Lady" unvanı almaz.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0` (Source-X referans ini `1` yazar)

### VENDORTRADETITLE

Satıcının tek tıklamada görünen adına (tek tık etiketi) mesleğini ("the blacksmith") ekle.

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### ALLOWBUYSELLAGENT

Liste okunamayacak kadar hızlı onaylanan alım/satımı (buy/sell agent programları) kabul et. Satıcı alım/satımına uygulanır.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0` (reddedilir)

## GLOBAL SCRIPT KANCALARI

### SPEECHSELF

Oyuncu konuştuğunda çalıştırılacak SPEECH blokları. Eşleşen `[SPEECH]` kalıplarını yerleşik oyuncu kelimelerinden önce çalıştırır.

- **Tür:** virgülle ayrılmış SPEECH defname'leri
- **Varsayılan:** `""` (Source-X örneği: `spk_player`)

### SPEECHPET

NPC/pet konuşmayı duyduğunda çalıştırılacak SPEECH blokları. Yerleşik servis kelimelerinden sonra, CHARDEF speech zinciri bitmeden çalışır.

- **Tür:** virgülle ayrılmış SPEECH defname'leri
- **Varsayılan:** `""` (Source-X örneği: `spk_pet`)

### EVENTSPET

Tüm NPC/pet karakterlere uygulanan global EVENTS listesi.

Trigger sırası: `EVENTS -> TEVENTS -> CHARDEF -> EventsPet`.

- **Tür:** virgülle ayrılmış EVENTS defname'leri
- **Varsayılan:** `""` (Source-X örneği: `e_npc_generic_event`)

### EVENTSNPC / EVENTSMONSTER / EVENTSANIMAL / EVENTSMOUNTABLE / EVENTSSHOP

Sphere 56T özel sürümü: NPC türüne göre EVENTS listeleri, `EventsPet`'ten sonra çalışır.

- `EventsNpc` - tüm NPC'ler
- `EventsMonster` - canavar beyinleri (monster, berserk, dragon)
- `EventsAnimal` - hayvan beyni
- `EventsMountable` - binilebilen gövdeler
- `EventsShop` - satıcı NPC'ler

56T paketinde canavar loot'u `EventsMonster`'dan gelir (`e_npc_monster @DeathCorpse`).

- **Tür:** virgülle ayrılmış EVENTS defname'leri
- **Varsayılan:** `""` (kapalı). Source-X'te yalnızca `EventsPet` vardır.

### EVENTSPLAYER

Tüm oyuncu karakterlere uygulanan global EVENTS listesi.

Trigger sırası: `EVENTS -> TEVENTS -> CHARDEF -> EventsPlayer`.

- **Tür:** virgülle ayrılmış EVENTS defname'leri
- **Varsayılan:** `""` (Source-X örneği: `e_player_generic_event,e_player_crafting_event`)

### EVENTSREGION

Region ve room event akışına eklenen opsiyonel global EVENTS.

- **Tür:** virgülle ayrılmış EVENTS defname'leri
- **Varsayılan:** `""` (kapalı)

### EVENTSITEM

Tüm eşyalara uygulanan opsiyonel global EVENTS listesi.

- **Tür:** virgülle ayrılmış EVENTS defname'leri
- **Varsayılan:** `""` (kapalı)

### COMMANDTRIGGER

Her komuttan önce çağrılan global fonksiyon (Source-X `f_oncommand`); `ARGS` = komut satırı, `RETURN 1` komutu tüketir. Paketin komut eklemesi/ezmesi içindir.

- **Tür:** metin (FUNCTION adı)
- **Varsayılan:** `""` (kapalı)
- **Örnek:** `CommandTrigger=f_oncommand`

## SUÇ & NOTORIETY (İTİBAR)

### CRIMINALTIMER

Suç işledikten sonra "suçlu" (gri) kalma süresi (DAKİKA, Source-X ile aynı birim). Süre dolunca bayrak temizlenir.

- **Tür:** tamsayı (dakika)
- **Varsayılan:** `3`

### MURDERMINCOUNT

Oyuncunun kırmızı (katil) olduğu cinayet sayısı. Her "masum" öldürme bir ekler; kırmızı = sayı bu değerden büyük.

- **Tür:** tamsayı
- **Varsayılan:** `5`

### MURDERDECAYTIME

Bir cinayet sayısının azalma süresi (saniye). Her bu sürede bir kill azaltılır.

- **Tür:** tamsayı (saniye)
- **Varsayılan:** `28800` (= 8 gerçek saat)

### PLAYEREVIL / PLAYERNEUTRAL

- `PLAYEREVIL` - Cinayeti olmayan bir oyuncunun kırmızı (evil) göründüğü karma eşiği.
- `PLAYERNEUTRAL` - Böyle bir oyuncunun gri (neutral) göründüğü karma eşiği.

Notoriety için kullanılır.

- **Tür:** tamsayı (karma)
- **Varsayılan:** `-8000` / `-2000` (Source-X ile aynı)

### LOOTINGISACRIME

Başka oyuncunun cesedini yağmalamak suç sayılır.

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### ATTACKINGISACRIME

Masum bir oyuncuya saldırmak suç sayılır. Korunan bölgede masum hedefe saldıran suçlu olur; kırmızı/gri hedef meşru müdafaa sayılır.

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### HELPINGCRIMINALSISACRIME

Suçluya yardım (iyileştirme/buff) suç sayılır.

- **Tür:** boolean (0/1)
- **Varsayılan:** `1` (Source-X ile aynı)

### GUARDLINGER

Çağrılan muhafızın kalma süresi (DAKİKA). Source-X bu anahtarı dakika okur (CServerConfig.cpp:1292: değer * 60 * MSECS_PER_SEC); eski bir ini'deki `GUARDLINGER=3` orada da üç dakikadır. Çağrılan muhafız süre dolunca kaldırılır.

- **Tür:** tamsayı (dakika)
- **Varsayılan:** `3` (Source-X CServerConfig.cpp:173)

### GUARDSINSTANTKILL

Muhafızlar suçluları savaşmak yerine anında öldürür.

- `0` = muhafızlar normal savaşır (güçlü oyuncular öldürebilir)
- `1` = muhafızlar hedefi anında öldürür (Source-X varsayılanı)

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### GUARDSONMURDERERS

Muhafızlar katillere (kırmızı) görür görmez saldırır; katiller ve petleri kapsanır.

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### SNOOPCRIMINAL

Görülen bir snoop'un suç sayılma yüzdesi (0-100) (Source-X `m_iSnoopCriminal`).

- **Tür:** sayı (0-100)
- **Varsayılan:** `100`

### TRADEWINDOWSNOOPING

Takas penceresindeki bir kabı açmak snooping sayılır.

- **Tür:** boolean (0/1)
- **Varsayılan:** `1` (`0` = snoop kontrolsüz açılır)

### NOTOTIMEOUT

Notoriety sorgu bayrağının sona erme süresi (saniye).

- **Tür:** tamsayı (saniye)
- **Varsayılan:** `30`
- **Not:** Yalnızca `SERV.NOTOTIMEOUT` ile okunur; notoriety'de süre takibi yok.

## ÖLÜM & DİRİLİŞ

### PACKETDEATHANIMATION

Ölen istemciye 0x2C ölüm ekranı paketini gönder. Hayalet görünümü her iki durumda da güncellenir.

- **Tür:** tamsayı (`0` = gönderme, `1` = gönder)
- **Varsayılan:** `1` (Source-X ile aynı)

### CORPSENPCDECAY

NPC ceset çürüme süresi (dakika).

- **Tür:** tamsayı (dakika)
- **Varsayılan:** `7`

### CORPSEPLAYERDECAY

Oyuncu ceset çürüme süresi (dakika). Ceset çürüyünce içindeki eşyalar yere düşer.

- **Tür:** tamsayı (dakika)
- **Varsayılan:** `7`

### HITPOINTPERCENTONREZ

Dirilişte geri verilen en yüksek HP yüzdesi.

- **Tür:** tamsayı (%)
- **Varsayılan:** `10`

### DEADCANNOTSEELIVING

Ölüler (hayaletler) yaşayanları göremez (Source-X modu).

- `0` = hayaletler her şeyi görür (standart)
- `1` = hayalet yaşayan NPC'leri göremez
- `2` = 1'e ek olarak NPC'ler sahibi olmayan yaşayan oyuncuları göremez

- **Tür:** tamsayı (0-2; eski "true/yes" 1 sayılır)
- **Varsayılan:** `0`

## KONTEYNER LİMİTLERİ

### CONTAINERMAXITEMS

Tek bir konteynerdeki en fazla eşya (sırt çantası, sandık, çanta...). Sınır aşılırsa bırakılan eşya çantaya geri döner ve "That container is full." mesajı gider.

- **Tür:** integer
- **Varsayılan:** `255` (Source-X `MAX_ITEMS_CONT`)

### BANKMAXITEMS

Banka kutusundaki en fazla eşya sayısı. Banka kutusuna bırakmada denetlenir.

- **Tür:** integer
- **Varsayılan:** `1000` (Source-X ile aynı)

### BANKMAXWEIGHT

Banka kutusundaki en fazla ağırlık (taş). Bankaya bırakmada toplam ağırlık kontrol edilir.

- **Tür:** integer (taş)
- **Varsayılan:** `1000` (Source-X ile aynı)

### CONTAINERMAXWEIGHT

Sıradan bir kabın (sandık, çanta) taşıyabildiği ağırlık (taş). Kaba bırakmada denetlenir.

- **Tür:** integer (taş)
- **Varsayılan:** `0` = kabın kendi sınırı yok
- **Not:** Kabın kendi `MODMAXWEIGHT`'i her zaman önce gelir; bu yalnızca kabın bir şey söylemediği durumda uygulanan isteğe bağlı genel sınırdır. Referansta böyle bir genel sınır yoktur (`CItemContainer.cpp:906`). Oyuncunun kendi çantası bunun yerine taşıma gücü + `BACKPACKOVERLOAD`'a tabidir.

### BACKPACKOVERLOAD

Taşıma limitinin üstüne kendi çantasına ne kadar yüklenebileceği (taş). Aç/kapa anahtarı değildir: çanta sahibinin taşıyabildiği + bu kadar alır (Source-X `CItemContainer.cpp:907`). Referans ini'nin kendi notu: bunu yükseltmek `STAMINALOSSOVERWEIGHT`'i SATIN ALMAKTIR — limitin üstündeki her adım stamina yakar.

- **Tür:** integer (taş; sıfırın altı = çantanın kendi sınırı yok)
- **Varsayılan:** `40`

**Yükün bedeli** (Source-X `CCharAct.cpp:4787-4829`): aşağıdaki dört ayarın dördü de YALNIZCA gerçekleşen adımda ödenir: yol arama denemesi, yalnızca kontrol çağrısı ve GM hiçbir şey ödemez.

### STAMINALOSSATWEIGHT

Adımın stamina yakmaya başladığı yük yüzdesi. Eşik DEĞİL, S-eğrisinin ORTA NOKTASIDIR: bu yükte adımların yarısı bir puan yakar, altında hızla düşer, üstünde hızla tırmanır (varyans 10). `200` = etki kapalı, çünkü %100'ü geçince aşırı-yük dalı devreye girer.

- **Tür:** integer (%)
- **Varsayılan:** `150`

### STAMINALOSSOVERWEIGHT

Taşıma limitinin ÜSTÜNDE atılan her adımın stamina bedeli. Bu dal şans değil, KESİNDİR: her adımda ödenir ve büyür — limitin üstündeki her 5 taş için +1, binekliyken toplamın üçte biri.

- **Tür:** integer
- **Varsayılan:** `5`

### RUNNINGPENALTY

Uçarken/süzülürken yük yüzdesine eklenen puan. Adı "koşma" der, kod `STATF_FLY | STATF_HOVERING`'e bakar (`CCharAct.cpp:4799`) — referansın kendi yorumu oradaki düz eklemeye FIXME der. Davranış eşlendi, ad değil: yayan koşmak fazladan bir şey ödetmez.

- **Tür:** integer (`0` = ceza yok)
- **Varsayılan:** `50`

### RUNNINGPENALTYOVERWEIGHT

Uçarken/süzülürken aşırı-yük bedelinin yüzde artışı.

BİLİNÇLİ SAPMA: referansın anahtar tablosu bu adı KOMŞUSUNUN alanına bağlamış (`CServerConfig.cpp:981-982`); orada bunu kurmak `RUNNINGPENALTY`'yi oynatır ve aşırı-yük artışı varsayılanından hiç çıkmaz. Bu bir tablo kopyala-yapıştır hatasıdır, sözleşme değil: tüketim yeri alanı adıyla okur (`CCharAct.cpp:4823`). Anahtar adını taşıdığı alana bağlandı.

- **Tür:** integer (%, `0` = artış yok)
- **Varsayılan:** `100` (referans ini `10` gönderir)

### DRAGWEIGHTMAX

Oyuncunun bir eşyayı eline alabileceği üst yük yüzdesi. Ölçüm kaldırma SONRASI oluşacak yük üzerinde yapılır (`CCharAct.cpp:2932`), yani çizgiyi aşacak eşya reddedilir. Kaçış kapısı kural kadar önemlidir: eşya kaldıranın KENDİ çantasındaysa reddedilmez, ayaklarının dibine düşer — yoksa aşırı yüklü oyuncu hiçbir şeyi yere bırakamazdı.

- **Tür:** integer (%; 0 veya altı = sınır yok)
- **Varsayılan:** `300`

### MAXITEMCOMPLEXITY

Bir `DUPE` satırının ÜST DÜZEY bir eşyadan kaç kopya üretebileceği; tek kareye yığılabilecek miktarın tavanı (Source-X `CItem.cpp:3636`). Kap İÇİNDE yapılan kopya bu sınıra tabi değildir.

- **Tür:** integer
- **Varsayılan:** `25`

### ITEMSMAXAMOUNT

Yığınlanabilir eşyalar için en fazla miktar (altın, reagent...). Yığın birleştirme ve `AMOUNT` yazımında uygulanır.

- **Tür:** integer
- **Varsayılan:** `60000`

### ALLOWNEWBTRANSFER

Newbie (`ATTR_NEWBIE`) eşyalar bir NPC'ye verilince bayrağını korur; sahip newbie eşyaları petinden geri alabilir.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `0`

### VERBOSEITEMBOUNCE

Geri seken bir eşyanın nereye gittiğini oyuncuya söyle (`msg_itemplace` iletisi).

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `0`

## EV & GEMİ SİSTEMİ

### MAXHOUSESPLAYER

Tek bir karakterin sahip olabileceği en fazla ev. Yerleştirmede karakter sınırı denetlenir.

- **Tür:** integer (`0` = ev yok)
- **Varsayılan:** `1`

### MAXHOUSESACCOUNT

Hesap başına en fazla ev (tüm karakterler birlikte). Hesaptaki her karakterin evleri sayılır.

- **Tür:** integer
- **Varsayılan:** `1`

### MAXHOUSESGUILD

Bir loncanın SAHİP OLABİLECEĞİ ev sayısı; kendi `MAXHOUSES`'ını yazmayan taş bu değeri alır (Source-X `CItemStone.cpp:24`).

- **Tür:** integer (`0` = lonca ev sahibi olamaz)
- **Varsayılan:** `1`

### MAXSHIPSPLAYER

Tek bir karakterin sahip olabileceği en fazla gemi.

- **Tür:** integer (negatif = sınırsız)
- **Varsayılan:** `1`

### MAXSHIPSACCOUNT

Hesap başına en fazla gemi.

- **Tür:** integer (negatif = sınırsız)
- **Varsayılan:** `1`

### MAXSHIPSGUILD

Bir loncanın sahip olabileceği gemi sayısı; kendi değerini yazmayan taş bunu alır.

- **Tür:** integer (0 veya üstü)
- **Varsayılan:** `1`

### MAXSHIPPLANKTELEPORT

Gemi iskelesinin karaya iniş noktası ararken yönü boyunca baktığı en uzak mesafe (tile).

- **Tür:** integer (tile)
- **Varsayılan:** `18`

### AUTOHOUSEKEYS

Ev yerleştirilince anahtar üret ve kapılarda anahtar ara. Kapalıyken anahtar verilmez; kapılar yalnızca ev yetkisine bakar.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `1`

### AUTOSHIPKEYS

Gemi yerleştirilince anahtar üret.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `1`

### AUTONEWBIEKEYS

Üretilen ev ve gemi anahtarları newbie olur (ölümde düşmez).

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `1`

## HESAP AYARLARI

### ACCAPP

Bilinmeyen hesaplar login sırasında otomatik oluşturulsun.

- **Tür:** integer (Source-X `ACCAPP_*` değerleri; SphereNet 0 dışındaki her değeri "açık" sayar)
  - `0` = yalnızca önceden oluşturulmuş hesaplar girebilir (canlı için önerilir)
  - 0 dışı = ilk giriş denemesinde hesap otomatik oluşturulur
- **Varsayılan:** anahtar hiç yazılmazsa `2` (açık; açılışta uyarı basılır)

### MAXCHARSPERACCOUNT

Hesap başına en fazla karakter yuvası. Hesap yöneticisi ve istemci karakter listesi tarafından kullanılır.

- **Tür:** integer (1-7 arasına kırpılır)
- **Varsayılan:** `5` (Source-X ile aynı)
- **Not:** UO istemcileri en fazla 7 karakter yuvasını destekler.

### MINCHARDELETETIME

Karakterin silinebilmesi için geçmesi gereken en kısa yaş (SANİYE, Source-X ile aynı birim). Kazara silmeyi önler. Counselor ve üstü hesaplar beklemez. Karakter seçim ekranındaki silme isteği bu yaşı denetler.

- **Tür:** integer (saniye; çarpım olarak yazılabilir: `7*24*60*60`)
- **Varsayılan:** `7*24*60*60` (= 7 gün); `0` = hemen sil

### MD5PASSWORDS

Hesap şifrelerini MD5 hash olarak sakla.

- **Tür:** boolean (`0`/`1`)
  - `0` = düz metin şifreler (güvensiz ama tüm araçlarla uyumlu)
  - `1` = tuzsuz MD5 özeti (zayıf: kaba kuvvete açık, aynı şifre = aynı özet)
- **Varsayılan:** `0` (Source-X); `0` iken açılışta uyarı basılır
- **Not:** Hesap dosyasında zaten MD5 özetleri olan bir shard `1`'de kalmalıdır. İki değer de güçlü saklama değildir (Source-X'te tuzlu biçim yoktur).

### AUTORESDISP

Hesabın çözünürlük/görüntü türünü istemci sürümüne göre ayarla. İstemci sürümü/çağı ResDisp'e çevrilip hesapta saklanır.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `1`

### AUTOPRIVFLAGS

Yeni oluşturulan hesabın başladığı PRIV bayrakları (Source-X `PRIV_*`).

- **Tür:** flags (Sphere kuralı)
- **Varsayılan:** `0` (referans ini örneği: `040`)

### GUESTSMAX

"GUEST" adıyla girişin kullanabileceği `GUESTn` misafir hesabı sayısı.

- **Tür:** integer (0 veya üstü)
- **Varsayılan:** `0` (misafir girişi kapalı)

### CLIENTLOGINMAXTRIES

Bir hesaba aynı adresten 15 sn içinde yapılabilecek hatalı giriş denemesi; aşılırsa adres geçici olarak yasaklanır.

- **Tür:** integer (0 veya üstü)
- **Varsayılan:** `0` (kapalı)

### CLIENTLOGINTEMPBAN

Bu geçici yasağın süresi (DAKİKA).

- **Tür:** integer (dakika)
- **Varsayılan:** `3`

### PACKET<n> / OUTPACKET<n>

Script paket filtreleri (x = opcode, ONDALIK, 0..254).

```
PACKET114=f_filter_war        gelen her 0x72 paketi için f_filter_war çalıştırır
OUTPACKET28=f_filter_speech   giden her 0x1C paketi için f_filter_speech çalıştırır
```

Fonksiyon sunucu nesnesi üzerinde çalışır:

- `ARGN1` = opcode
- `ARGS` = istemci IP
- `ARGO` = istemci
- `LOCAL.CONNECTIONTYPE`
- `LOCAL.NUM` (uzunluk)
- `LOCAL.STR` (byte'lar metin olarak)
- `LOCAL.ACCOUNT`, `LOCAL.CHAR`
- `LOCAL.0` .. `LOCAL.<NUM-1>` (her byte)

`RETURN 1` paketi düşürür: sunucu işlemez / göndermez. Fonksiyon adı en fazla 30 karakter. Kayıtlı olmayan opcode'lar da filtrelenir.

- **Varsayılan:** yok
- **Not:** Source-X `CClient::xPacketFilter` / `xOutPacketFilter` ile eşleşir.

## AĞ PERFORMANS AYARLARI

### USEASYNCNETWORK

Asenkron soket G/Ç kullan.

- **Tür:** integer (0-2)
  - `0` = senkron G/Ç
  - `1` = istemci başına 1 thread ile async
  - `2` = thread pool ile async
- **Varsayılan:** `0`
- **Not:** Sunucu yapılandırması okumaz. Ağ ana döngüde bloklamayan soketlerle işlenir (Poll -> Receive -> Process -> Send); async ağ yoktur. Resync mekanizması (paket `0x22`, `.RESYNC`) mevcuttur.

### NETWORKTHREADS

Adanmış ağ G/Ç thread sayısı.

- **Tür:** integer (`0` = ağı ana thread yönetir)
- **Varsayılan:** `0`
- **Not:** Yalnızca `SERV.NETWORKTHREADS` ile okunur; bilinçli no-op (ağ ana döngüde işlenir). Girdi ana döngüde seri işlenir. Yalnızca çıkış flush'ı, 128 veya daha fazla aktif bağlantıda bu değerden bağımsız olarak (tüm çekirdeklerde) paralel çalışır.

### NETWORKTHREADPRIORITY

Ağ thread'lerinin önceliği (0=Normal .. 4=GerçekZaman).

- **Tür:** integer (0-4)
- **Varsayılan:** `0`
- **Not:** Sunucu yapılandırmasında tanımlı değil; etkisi yoktur.

### MAXPACKETSPERTICK

Tick başına, kuyruk başına bir istemciye GÖNDERİLEN en fazla paket (Source-X: çıkış sınırı, `packet.h` `NETWORK_MAXPACKETS`).

- **Tür:** integer
- **Varsayılan:** `50` (Source-X ile aynı)
- **Not:** Yalnızca `SERV.MAXPACKETSPERTICK` ile okunur. Gelen paketler sınırlanmaz: istemcinin gönderdiği her paket aynı geçişte işlenir ve paket sayısı yüzünden bağlantı düşürülmez (Source-X ile aynı). Girdi hacmini `MAXSIZECLIENTIN` (10 sn'de byte) sınırlar. Gönderim yolu soketin kabul ettiği kadarını boşaltır ve bu değeri kullanmaz. Eski `FLOODDETECTIONCOUNT` ve `FLOODDETECTIONWINDOWMS` anahtarları kaldırıldı; hâlâ içeren bir ini normal yüklenir ve sunucu bunların yok sayıldığını bir kez bildirir.

### MAXSIZECLIENTIN

Bir istemciden 10 saniyede alınabilecek en fazla veri (byte). Aşılırsa `f_onclient_exceed_network_quota` `ARGN1=2` ile çalışır:

- `RETURN 0` = tut + logla
- `RETURN 1` = sessizce tut
- diğer = at + logla

- **Tür:** integer (byte, `0` = kapalı)
- **Varsayılan:** anahtar yoksa `10000`

### MAXSIZECLIENTOUT

Bir istemciye 10 saniyede gönderilebilecek en fazla veri (byte). Aşılırsa `f_onclient_exceed_network_quota` `ARGN1=1` ile çalışır.

- **Tür:** integer (byte, `0` = kapalı)
- **Varsayılan:** anahtar yoksa `80000`

### MAXSIZEPERTICK

Tick başına toplam ağ G/Ç sınırı (byte).

- **Tür:** integer (byte, `0` = sınırsız)
- **Varsayılan:** `0`
- **Not:** Sunucu yapılandırmasında tanımlı değil; etkisi yoktur.

### MAXQUEUESIZE

Öncelik kuyruğu başına paket sayısı; aşılınca yeni paketler bir alt önceliğe kaydırılır. Paket atmaz, bağlantıyı da kesmez.

- **Tür:** integer
- **Varsayılan:** `75`
- **Not:** Sunucu yapılandırmasında tanımlı değil. SphereNet her gönderimde tüm öncelik kuyruklarını boşaltır; kaydırmanın kısacağı bir şey yoktur, yalnızca paket sırasını bozardı. Gönderim kuyruğunun derinlik sınırı yoktur: bir patlama (ALLSHOW ile kalabalık ekran) kuyrukta kalır ve istemci okudukça gider. 1024 paket / 256 KB üstünde önce paylaşılan konuşma ve ses yayınları atılır. İstemci yalnızca soketi veri beklerken 60 saniye hiç veri almazsa ya da 64 MB gönderilmemiş veri birikirse düşürülür.

### NETTTL

Boşta bağlantıların yaşam süresi (saniye); bu süre hareketsiz kalan bağlantı kapatılır. Bir IP'nin bağlantı geçmişinin ömrü de budur.

- **Tür:** integer (saniye; 10'un altı açılışta uyarı)
- **Varsayılan:** `300`

### MAXCONNECTREQUESTSPERIP

Bir IP'nin, bağlantı geçmişi yaşadığı sürece yapabileceği bağlantı denemesi. Azalmaz; IP başarılı giriş yaptığında ya da geçmiş silinince (son bağlantı kapandıktan `NETTTL` sonra) sıfırlanır. Her giriş iki bağlantıdır (login + game).

- **Tür:** integer (`0` = kapalı)
- **Varsayılan:** `50` (Source-X yerleşik değeri 5, referans ini 50)

### MAXPINGS

Ping gibi sayılan bağlantı denemesi sınırı; `max(30, NETTTL/5)` saniyede bir azalır.

- **Tür:** integer
- **Varsayılan:** `15`

### TIMEOUTINCOMPLETECONN

Kendini tanıtmamış (giriş paketini göndermemiş) bir bağlantının kapatılmadan önce bekleyebileceği süre (ms).

- **Tür:** integer (ms; çarpım yazılabilir: `5*1000`)
- **Varsayılan:** `5000` (`0` = kapalı)

### DEADSOCKETTIME

Source-X: ölü soketin kapatılma süresi (Source-X'te DAKİKA, varsayılan 5).

- **Tür:** integer
- **Varsayılan:** `300`
- **Not:** Yalnızca `SERV.DEADSOCKETTIME` ile okunur; boşta bağlantılar `NETTTL` ile kapanır.

### RTTPINGINTERVALMS

Sunucunun istemciye kendiliğinden `0x73` ping gönderme aralığı (ms).

- **Tür:** integer (ms, `0` = kapalı)
- **Varsayılan:** `0`
- **Not:** DİKKAT: ClassicUO gelen her `0x73`'ü kendi ping ölçerine katar; sunucu pingleri istemcide anlamsız (çok büyük) bir ping gösterir. Sunucu tarafı RTT ölçümünü hiçbir şey kullanmaz; istenmeyen pingleri yok sayan bir istemci yoksa kapalı tutun.

### CUOSTATUS / UOGSTATUS

ConnectUO / UOGateway durum sorgularına cevap ver.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `1`

### USEHTTP

Web durum sayfasını sun (`SERVPORT+2` üzerinde).

- **Tür:** integer (`0` = kapalı, diğer her değer açık)
- **Varsayılan:** `2` (Source-X)

### USEEXTRABUFFER

Bağlantı başına ek çıktı tamponu ayır.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `0`
- **Not:** Sunucu yapılandırmasında tanımlı değil; etkisi yoktur.

### USEPACKETPRIORITY

Giden paketler için öncelik kuyruğu.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `0`
- **Not:** Sunucu yapılandırmasında tanımlı değil. Giden paketler her zaman bağlantı başına öncelik kuyruklarından geçer; bu anahtar onları ne açar ne kapatır.

## KOMUTLAR, GÖREVLİLER & YÖNETİM

### COMMANDPREFIX

Oyun içi GM komutlarının önek karakteri (yalnızca ilk karakter sayılır).

- **Tür:** string (tek karakter)
- **Varsayılan:** `"."`
- **Örnek:** `.` ile oyunda `.ADD 0x0F51` yazmak eşya oluşturur.

### DEFAULTCOMMANDLEVEL

Yeni hesapların varsayılan PrivLevel'ı (yeni oluşturulan hesaplara uygulanır).

- **Tür:** integer (0-7)

  | Değer | Seviye |
  |---|---|
  | `0` | Misafir (Guest) |
  | `1` | Oyuncu (Player) |
  | `2` | Danışman (Counselor) |
  | `3` | Kâhin (Seer) |
  | `4` | GM |
  | `5` | Geliştirici (Dev) |
  | `6` | Yönetici (Admin) |
  | `7` | Sahip (Owner) |

- **Varsayılan:** `1` (Oyuncu; Oyuncu'nun üstü açılışta uyarı basar)

### COMMANDLOG

Komutları loglanacak en düşük yetki seviyesi. Oyun içi komutlara ve telnet konsoluna uygulanır.

- **Tür:** integer (PrivLevel; `-1` = hiçbiri)
- **Varsayılan:** `0` (herkesin komutları)

### CANSEESAMEPLEVEL

Görevlilerin gizli (invisible/hidden) görevlileri görme kuralı.

- **Tür:** integer
  - `0` = eşit veya yüksek seviye görür
  - `1` = yalnızca daha yüksek seviye görür
  - `2`-`7` = bu seviye ve üstü herkesi görür
- **Varsayılan:** `0`

### EXPERIMENTAL

Source-X deneysel özellik bayrakları.

- **Tür:** hex/integer
- **Varsayılan:** `0`

SphereNet'in okuduğu bitler:

| Bit | Ad | Etki |
|---|---|---|
| `0x0000040` | `EF_Item_Strict_Comparison` | Kaynak eşleştirmede tahta/kütük ve deri/post birbirinin yerine geçmez. |
| `0x0002000` | `EF_DamageTools` | Aletler kullanıldıkça aşınma hasarı alır. |
| `0x0080000` | `EF_WalkBypassMonsters` | Her mobile "mobile'ları yok say" mod biti (`0x10`) ile gönderilir; oyuncular tam stamina olmadan NPC'lerin üstünden geçebilir (normalde istemci bunu yalnızca görevlilere izin verir). UYARI: sunucu yürüme kontrolü değişmez; kaynak tüketebilir ve istemcide hareket titremesine yol açabilir. |

Diğer bitler yalnızca `SERV.EXPERIMENTAL` ile okunur.

### TELNETLOG

Telnet konsol komutlarını logla.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `1`

### SECURE

Source-X güvenli mod. SphereNet'te donma izleyicisi yalnızca bu açıkken çalışır.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `1`

### FREEZERESTARTTIME

Ana döngü bu kadar süre ilerlemezse sunucu donmuş sayılır ve çıkış kodu 3 ile kapanır (Host yeniden başlatır).

- **Tür:** integer (saniye, `0` = izleyici kapalı)
- **Varsayılan:** `60`
- **Not:** `SECURE=0` iken izleyici çalışmaz.

### LOGMASK

Source-X log kategori maskesi.

- **Tür:** hex/integer (`0x` öneki gerekir; referans ini'nin `0fff00` yazımı OKUNAMAZ)
- **Varsayılan:** `0x3F00`
- **Not:** SphereNet log filtrelemesini `LOGFILELEVEL` ile yapar; bu maske yalnızca `SERV.LOGMASK` ve HEARALL biti (`0x2000`) için tutulur.

### HEARALL

`LOGMASK`'taki oyuncu-konuşması bitini (`0x2000`) açar/kapatır.

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `LOGMASK`'taki bit (varsayılan maskede açık)
- **Not:** Oyun içi `PRIV_HEARALL` (görevlinin her konuşmayı duyması) ayrıdır ve bir karakter bayrağıdır. Bu ini anahtarı yalnızca `SERV.HEARALL` okumasını/geçişini etkiler. Hiçbir konuşma loglaması bu bite bağlı değil.

### SENTRYDSN

Hataların gönderileceği Sentry projesinin DSN adresi (program başlangıcında Sentry SDK tarafından kullanılır).

- **Tür:** string
- **Varsayılan:** `""` (kapalı)
- **Not:** `//` satırın her yerinde yorum başlatır (Source-X gibi); DSN'i ŞEMASIZ yazın: `SENTRYDSN=key@o1.ingest.sentry.io/123` — `https://` otomatik eklenir.

## KONUŞMA, SOHBET & MESAJLAR

### CHATFLAGS

Sohbet sistemi bayrakları (Source-X `CHATF_*`).

- **Tür:** hex/integer (`0x` öneki gerekir)

  | Bit | Ad | Etki |
  |---|---|---|
  | `0x01` | AUTOJOIN | ilk statik kanala katıl |
  | `0x02` | CHANNELCREATION | kanal oluşturma |
  | `0x04` | CHANNELMODERATION | kanal moderasyonu |
  | `0x08` | CUSTOMNAMES | özel sohbet adları |
  | `0x10` | GLOBALCHAT | genel sohbet (7.0.62.2+) |

- **Varsayılan:** `0`
- **Not:** SphereNet yalnızca `0x10` GLOBALCHAT'i uygular, diğer bitler yalnızca `SERV.CHATFLAGS` ile okunur.

### CHATSTATICCHANNELS

Sohbet sisteminde her zaman var olan kanallar (virgülle).

- **Tür:** string
- **Varsayılan:** `"General, Help, Trade, Looking For Group"`

### ARRIVEDEPARTMSG

Oyuncu giriş/çıkış duyurusu (login/logout sırasında).

- **Tür:** integer
  - `0` = kapalı
  - `1` = oyunculara duyur
  - `2` = yalnızca görevlilere
- **Varsayılan:** `1`

### CHARTAGS

Tek tıklamada karakter adına `[npc]`, `[tame]` gibi etiketler ekle (tek tık etiketi).

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `0` (AllShow'daki GM zaten görür)

### SUPPRESSCAPITALS

Oyuncunun tamamı büyük harfli konuşmasını küçült (5 harften uzun).

- **Tür:** boolean (`0`/`1`)
- **Varsayılan:** `0`

### SPEECHOTHER

Source-X'in okuyup sakladığı ama hiç kullanmadığı SPEECH listesi.

- **Tür:** string
- **Varsayılan:** `""`
- **Not:** Yalnızca `SERV.SPEECHOTHER` ile okunur (Source-X de kullanmaz).

### MEDIUMCANHEARGHOSTS

Yaşayanların hayalet konuşmasını anladığı Spirit Speak değeri (onda birlik: `1000` = 100.0).

- **Tür:** integer
- **Varsayılan:** `1000`

### EMOTEFLAGS

Otomatik emote'ları kimin gördüğü (Source-X `EMOTEF_*`).

- **Tür:** hex/integer (`0x` öneki gerekir)

  | Bit | Ad | Etki |
  |---|---|---|
  | `0x01` | ATTACKER | "%s is attacking %s!" yalnızca saldırılan karaktere |
  | `0x02` | POISON | zehir emote'unu yalnızca zehirlenen görür |
  | `0x04` | DESTROY | eşya yok olma emote'u yalnızca sahibine |
  | `0x08` | HIDE_EAT_PLAYER | oyuncu yerken emote yok |
  | `0x10` | HIDE_EAT_NPC | NPC yerken emote yok |

- **Varsayılan:** `0`
- **Not:** SphereNet yalnızca `0x02` POISON'u uygular.

### CONTEXTMENULIMIT

Bir bağlam (context) menüsünde gönderilen en fazla satır.

- **Tür:** integer (0-255)
- **Varsayılan:** `15`

## SCRIPT & DÜNYA KURALLARI

### MAXLOOPTIMES

Tek bir script döngüsünün (`WHILE`/`FOR`...) en fazla tur sayısı.

- **Tür:** integer (`0` = sınırsız)
- **Varsayılan:** `100000` (Source-X referans ini `10000` ile gelir)

### AREAFLAGS

ROOMDEF'lerin çevrelerindeki AREA'dan miras aldıkları (Source-X `AREAF_*`). Dünya yüklendikten sonra bir kez uygulanır.

- **Tür:** flags (Sphere kuralı)
  - `01` = EVENTS
  - `02` = FLAGS
  - `04` = TAG'ler
- **Varsayılan:** `02`

### ADVANCEDLOS

Yeni görüş hattı (line-of-sight) algoritmasının kimde açık olduğu. Kalite kademesi değil, BİT MASKESİ (Source-X `CServerConfig.h:474-476`, `CCharLOS.cpp:17`).

- **Tür:** integer bit maskesi (0-3)
  - `0x00` KAPALI
  - `0x01` OYUNCU
  - `0x02` NPC
  - `0x03` ikisi
- **Varsayılan:** `0`
- **Not:** Bit kapalıysa o tür bakan eski yöntemi kullanır: ayak hizasında kare kare yürüme (Source-X `CanSeeLOS`, varsayılan); bit açıksa göz hizasında ışın (`CanSeeLOS_New`).

### DISTANCEFORMULA

Mesafe hesaplama yöntemi (Source-X `CServerConfig.h:479-481`, `CPointBase.cpp:151+`).

- **Tür:** integer (0-2)
  - `0` = köşegen yok, Z yok — nesne merkezde KARE bir alan (UO standardı)
  - `1` = köşegen sayılır, Z yok — DAİRESEL bir alan
  - `2` = 1 gibi, ama her mesafe kontrolü Z'yi de hesaba katar
- **Varsayılan:** `0`
- **Not:** İstemci görüş alanı (`GetDistSight`) her zaman kare kalır.

### FLIPDROPPEDITEMS

Yere bırakılan eşya çevrilmiş grafiğine döner. Taşınamayan eşyaya asla uygulanmaz (Source-X `CCharAct.cpp:3266`).

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### CANUNDRESSPETS

Sahibinin petinin ÜZERİNDEKİ eşyayı çıkarmasına izin ver. Kuralın büyük kısmı zaten SAHİPLİK: sahip petinin ÇANTASINA her halükârda uzanabilir; bu anahtar yalnızca petin GİYDİĞİNİ de çıkarabilir mi onu söyler (`CCharAct.cpp:2957`). Hiçbiri başkasına açık değildir. 2026-09-14 öncesinde bu kapı "başka bir OYUNCU değilse" diye yazılmıştı, yani dünyadaki her NPC herkese açıktı — bir yabancının ejderhasının zırhı dahil.

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### CANPETSDRINKPOTION

Petin üstüne bırakılan iksiri içmesi. Kapalıyken şişe, diğer her hediye gibi petin çantasına gider (`CCharNPCAct.cpp:2145`). Yaralı bir hayvanı dövüş ortasında iyileştirmenin yolu.

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### VENDORMARKUP

Satıcının kâr marjı yüzdesi (daha özel bir şey söylenmediğinde). Sıra: satıcının kendi TAG'i -> bölgenin TAG'i -> bu (`CCharNPCStatus.cpp:366`). `1` = marj yok. `0` = alışveriş BEDAVA — referans ini'nin kendi uyarısı.

- **Tür:** integer (%)
- **Varsayılan:** `15`

### VENDORMAXSELL

Satıcının tek seferde bir satırdan verdiği en fazla adet. Sınır liste KURULURKEN uygulanır (`send.cpp:1249`), tık geldikten sonra değil: istemci shard'ın izin vermediği bir sayıyı hiç göstermez.

- **Tür:** integer (0 veya altı = sınır yok)
- **Varsayılan:** `255`

### LOSTNPCTELEPORT

NPC evinden bu kadar uzaklaşırsa geri ışınlanır (tile). Tasma değil, EMNİYET KEMERİ: mesafenin hem bunu hem yaratığın kendi dolaşma menzilini aşması gerekir (`CCharNPCAct.cpp:1550`); yoksa geniş dolaşan bir spawn dar bir global ayarla eve sürüklenirdi. `@NPCLostTeleport` veto edebilir.

- **Tür:** integer (tile, `0` = kapalı)
- **Varsayılan:** `50`

### NPCSHOVENPC

Bir NPC başka bir NPC'nin üstünden geçebilir mi? Oyuncular yaratıkları iter; yaratıklar birbirini tutar (`CCharAct.cpp:4624`). Tek bir yaratık `TAG.OVERRIDE.SHOVE` ile muaf tutulabilir. Kural olmadan muhafız, arkasında sıkışması gereken kalabalığın içinden geçip gider.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

### TOOLTIPMODE

Tooltip (AOS özellik) görüntüleme modu. `0xDC` revision ve `0xD6` property list akışını belirler.

- **Tür:** integer (0-2)
  - `0` = kapalı (tooltip gönderilmez)
  - `1` = sürüm/hash gönder; değişmiş veya istenmişse tam tooltip gönder
  - `2` = her zaman tam tooltip gönder
- **Varsayılan:** `1` (`TOOLTIPMODE_SENDVERSION`, Source-X gibi)

### TOOLTIPCACHE

Hesaplanan tooltip verisinin yeniden hesaplanmadan kullanıldığı süre (saniye).

- **Tür:** integer (saniye)
- **Varsayılan:** `30`

### TELEPORTEFFECTNPC / TELEPORTEFFECTPLAYERS / TELEPORTEFFECTSTAFF

Işınlanmada gösterilen görsel efekt id'si, ışınlanana göre (`0` = efekt yok). Işınlananın efekti gösterilir. Işınlanmaya kendi efektini veren script bunları ezer (`CCharSpell.cpp:178`).

- **Tür:** integer (efekt grafik id; `0x` öneki veya baştaki sıfır = hex)
- **Varsayılan:** NPC `0x376A`, oyuncu `0x3728` (kayboluş / vanish), görevli `0x3709` (flamestrike) — Source-X `CServerConfig.cpp:88`

### TELEPORTSOUNDNPC / TELEPORTSOUNDPLAYERS / TELEPORTSOUNDSTAFF

Işınlanma sesi (`0` = ses yok).

- **Tür:** integer (ses id; `0x` öneki veya baştaki sıfır = hex)
- **Varsayılan:** NPC `0x01FE`, oyuncu `0x01FE`, görevli `0x01F3` — Source-X `CServerConfig.cpp:89`

### GENERICSOUNDS

Genel aksiyon seslerini çal (vuruş, ıskalama, parry ...).

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### DECAYTIMER

Yerdeki eşyaların varsayılan çürüme süresi (dakika).

- **Tür:** integer (dakika, en az 1)
- **Varsayılan:** `30`
- **Not:** Source-X de bu değeri dakika olarak okur.

## HAREKET

### WALKBUFFER

Hareket jeton kovasının kapasitesi (izin verilen ani adım patlaması). Hız hilesine karşı tampon: kabul edilen her adım 1 jeton harcar, boş kova `WALKREGEN` hızında dolar. Çok düşük değerler (örn. `2`) koşan oyuncuyu boğar: adımlar `walk_buffer` ile reddedilir ve istemci geri sıçrar ("takılma/rubber-band"). Bu, jeton kovalı hareket geçididir.

- **Tür:** integer (jeton, en az 1; 5'in altı açılışta uyarı)
- **Varsayılan:** `75`
- **Not:** Source-X'te bu anahtar saniyenin onda biri cinsinden bir süredir; SphereNet jeton sayısı olarak okur.

### WALKREGEN

Saniyede yenilenen hareket jetonu (RATE, periyot DEĞİL). Koşan oyuncu ~5 adım/sn ister; değer en az o kadar olmalı (`25` bol pay bırakır). `1` gibi düşük değerler saniyede 1 adıma kilitler.

- **Tür:** integer (saniyede jeton; 5'in altı açılışta uyarı)
- **Varsayılan:** `25`

### WALKDELAYFOOT / WALKDELAYMOUNT / RUNDELAYFOOT / RUNDELAYMOUNT

Yaya/binekli yürüme ve koşma adımları arasındaki en kısa süre (ms) (hareket hız sınırı). İstemcinin kendi adım hızıyla aynı olmalıdır; sonraki adım (değer - 30 ms) sonra kabul edilir.

- **Tür:** integer (ms; `WALKDELAYFOOT` 0 veya altı = açılışta uyarı)
- **Varsayılan:** `400` / `200` / `200` / `100` (istemci hızları)

### MOVEMENTCREDITENABLED

Jeton kovası yerine kredi tabanlı hareket sistemi (isteğe bağlı). Açıkken hareket geçidinde `WALKBUFFER`/`WALKREGEN` yerine `MOVEMENTCREDIT*` kullanılır.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

### MOVEMENTCREDITBASEMS

Kredi sisteminde bir adımın temel maliyeti (ms).

- **Tür:** integer (ms; 50'nin altı açılışta uyarı)
- **Varsayılan:** `200`
- **Not:** Yalnızca `MOVEMENTCREDITENABLED=1` iken kullanılır.

### MOVEMENTCREDITMAXMS

Kredi kovasının biriktirebileceği en fazla süre (ms).

- **Tür:** integer (ms)
- **Varsayılan:** `1400`
- **Not:** Yalnızca `MOVEMENTCREDITENABLED=1` iken kullanılır.

### MOVEMENTQUEUECAPACITY

Erken gelen adımların bekletildiği kuyruğun boyu.

- **Tür:** integer (50'nin üstü hız hilesini gizleyebilir, açılışta uyarı)
- **Varsayılan:** `10`

### SPEEDHACKDETECTIONENABLED

Hareket geçmişinden hız hilesi tespiti.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

### SPEEDHACKRATETHRESHOLD

Beklenen adım hızının kaç katının ihlal sayıldığı.

- **Tür:** noktalı ondalık sayı (`1.5`)
- **Varsayılan:** `1.5`
- **Not:** Yalnızca `SPEEDHACKDETECTIONENABLED=1` iken kullanılır.

### SPEEDHACKBURSTWINDOW

Hız hesabında bakılan ardışık adım sayısı.

- **Tür:** integer
- **Varsayılan:** `3`
- **Not:** Yalnızca `SPEEDHACKDETECTIONENABLED=1` iken kullanılır.

### SPEEDHACKHISTORYSIZE

Saklanan hareket geçmişinin uzunluğu (adım).

- **Tür:** integer
- **Varsayılan:** `20`

### SPEEDHACKCOOLDOWNMS

Bir ihlalden sonra yenisi bildirilmeden geçen süre (ms).

- **Tür:** integer (ms)
- **Varsayılan:** `60000`
- **Not:** Yalnızca `SPEEDHACKDETECTIONENABLED=1` iken kullanılır.

### MOUNTHEIGHT

Binekli (veya süzülen gargoyle) karakter 4 birim daha uzun sayılır ve tavanı bu yüksekliğe değen yere adım atamaz. GM ve AllMove muaftır.

- **Tür:** boolean (0/1)
- **Varsayılan:** `0`

## IŞIK, HAVA & MEVSİMLER

### NOWEATHER

Hava olayları (yağmur, kar) hiç olmasın mı? Bayrak yeni hava ATILMASINI ve istemciye SÖYLENMESİNİ durdurur; scriptin bir bölgeye kurduğu hava saklanır ve okunabilir (Source-X `CSector.cpp:861`, `CClientMsg.cpp:526`).

- **Tür:** boolean (0/1)
- **Varsayılan:** `1` — çıplak Source-X'te hava YOKTUR; hava isteyen shard `0` yazar (bu paket öyle yapar).

### LIGHTDAY

Gündüz aydınlık seviyesi (`0` = en parlak, `30` = en karanlık). Dünya ışık hesabında kullanılır.

- **Tür:** integer (0-30)
- **Varsayılan:** `0`

### LIGHTNIGHT

Gece aydınlık seviyesi (`0` = en parlak, `30` = en karanlık). Dünya ışık hesabında kullanılır.

- **Tür:** integer (0-30)
- **Varsayılan:** `25`

### DUNGEONLIGHT

Zindan bölgelerindeki aydınlık seviyesi.

- **Tür:** integer (0-30)
- **Varsayılan:** `27`

### ALLOWLIGHTOVERRIDE

Görevli ya da scriptin bir sektöre sabitlediği `LIGHT` değeri gündüz/gece hesabını ezer. `0` = sektör her zaman saate uyar.

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### SEASONMODE

Mevsim akış modu.

- **Tür:** string veya integer
  - `Auto` / `0` = mevsimler aralık doldukça otomatik ilerler
  - `Manual` / `1` = mevsim sabit kalır; script/yönetici değiştirir
- **Varsayılan:** `Auto`

### SEASONDEFAULT

Sunucu açıldığında başlayan mevsim. Login/resync/tick bu mevsim durumundan başlar.

- **Tür:** integer (0-4)
  - `0` = İlkbahar
  - `1` = Yaz
  - `2` = Sonbahar
  - `3` = Kış
  - `4` = Desolation
- **Varsayılan:** `0`

### SEASONCHANGEINTERVALMINUTES

Auto modda mevsim geçiş aralığı (dakika).

- **Tür:** integer (`0` = otomatik döngü kapalı, `1`+ = dakika)
- **Varsayılan:** `30`
- **Not:** Manual modda yok sayılır.

## DURUM KAYDI & MAKROLAR

### STATERECORDINGENABLED

Karakter durum kayıt sistemi (SQLite).

- **Tür:** boolean (0/1)
  - `0` = kapalı — SQLite DB oluşturulmaz, performans etkisi sıfır
  - `1` = açık — karakterlerin pozisyon/ekipman snapshot'ları kaydedilir; yönetici `.SREC` ile izleyebilir, paylaşılabilir kayıtlar oluşturulabilir
- **Varsayılan:** `0` (kapalı)

### STATERECORDPLAYERSONLY

Yalnızca oyuncu karakterlerini kaydet (NPC'leri atla).

- **Tür:** boolean (0/1)
  - `0` = tüm karakterler kaydedilir (çok NPC'de performans etkisi!)
  - `1` = yalnızca oyuncular (önerilen)
- **Varsayılan:** `1`
- **Not:** 100K+ NPC'li dünyalarda `0` ciddi CPU yükü oluşturur.

### STATERECORDMOVESCANMS

Hareket tarama aralığı (ms). Düşük değer = daha hassas hareket kaydı, daha çok CPU/disk.

- **Tür:** integer (ms, en az 500)
- **Varsayılan:** `2000`

### STATERECORDSNAPSHOTMS

Tam durum snapshot aralığı (ekipman, HP, pozisyon) (ms).

- **Tür:** integer (ms, en az 5000)
- **Varsayılan:** `15000`

### MACROENABLED

Oyuncu makro kayıt/oynatma sistemi. Tüm aksiyonlar normal oyun motoru üzerinden çalışır — skill check, timer, ağırlık limiti, başarı/başarısızlık hepsi standart kurallarla işler.

- **Tür:** boolean (0/1)
  - `0` = kapalı — `.MACRO` komutu çalışmaz
  - `1` = açık — oyuncular aksiyonlarını kaydedip tekrarlayabilir (mining, crafting ...)
- **Varsayılan:** `1`

### MACROMAXSTEPS

Bir makroda kaydedilebilecek en fazla adım.

- **Tür:** integer (5-200 arasına kırpılır)
- **Varsayılan:** `50`

### MACROMAXLOOPMINUTES

Döngüdeki bir makronun en uzun çalışma süresi (dakika).

- **Tür:** integer (1-1440 arasına kırpılır)
- **Varsayılan:** `120` (= 2 saat)

## EŞYA DAYANIKLILIĞI

### ITEMDURABILITYENABLED

Savaşta silah ve zırh hasar alır. Saldırı çözümlendikten sonra silaha ve rastgele bir zırha uygulanır.

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### ITEMDURABILITYLOSSCHANCE

Her vuruşta hasar alma şansı (%).

- **Tür:** integer (0-100)
- **Varsayılan:** `25`

### ITEMDURABILITYLOSSMIN / ITEMDURABILITYLOSSMAX

Kaybedilen dayanıklılık aralığı.

- **Tür:** integer (MIN 0 veya üstü; MAX en az MIN)
- **Varsayılan:** `1` / `1`

### ITEMBREAKONZEROHITS

`0` = kırılmaz, yalnızca 0'da kalır; `1` = kırılır ve silinir.

- **Tür:** boolean (0/1)
- **Varsayılan:** `1`

### ITEMDEFAULTHITS

`HITS` değeri olmayan silah/zırh için varsayılan dayanıklılık.

- **Tür:** integer (en az 1)
- **Varsayılan:** `50`
- **Not:** Değer okunup savaş motorunun varsayılan dayanıklılık ayarına atanıyor ama hiçbir yol okumuyor; bu yüzden şu an etkisi yok.

## VERİTABANI (MySQL / SQLite / PostgreSQL)

Scriptlerin `DB.*` nesnesiyle kullandığı bağlantılar. İki yol var:

1. Klasik tek bağlantı, `[SPHERE]` içinde (Source-X uyumlu). `MYSQL` 0 dışı ve `MYSQLHOST` dolu ise `default` adlı bir bağlantı kurulur ve açılışta bağlanır.
2. Adlandırılmış bağlantılar, `[SPHERE]` bloğunun DIŞINDA, kendi bölümlerinde (aşağıya bakın).

### MYSQL

Klasik MySQL bağlantısını aç. `SERV.MYSQL` bağlı olup olmadığını döndürür.

- **Tür:** integer (0 dışı = açık)
- **Varsayılan:** `0`

### MYSQLHOST / MYSQLUSER / MYSQLPASSWORD / MYSQLDATABASE

Klasik bağlantının sunucusu, kullanıcısı, şifresi ve veritabanı adı.

- **Tür:** string
- **Varsayılan:** `""` (port her zaman 3306; farklı port için 2. yolu kullanın)
- **Not:** Yalnızca `MYSQL` 0 dışıyken kullanılır.

### MYSQLCHARSET

Klasik bağlantının oturum karakter seti; bağlandıktan sonra `SET NAMES` ile gönderilir (aşağıdaki 2. yoldaki `CHARSET` ile aynı).

- **Tür:** string
- **Varsayılan:** `""` (utf8mb4)

### [MYSQL <name>] / [SQLITE <name>] / [PGSQL <name>]

Adlandırılmış bağlantılar, `[SPHERE]` bloğunun DIŞINDA, kendi bölümlerinde: `[MYSQL <ad>]`, `[SQLITE <ad>]` veya `[PGSQL <ad>]`. `default` adlı bölüm 1. yoldaki bağlantının üstüne yazar. Bölümler `[EOF]`'tan ÖNCE olmalıdır. ini'deki örneği kullanmak için satır başındaki `// ` işaretlerini kaldırıp `[EOF]`'un üstüne taşıyın.

Anahtarlar (hepsi isteğe bağlı):

| Anahtar | Anlamı | Varsayılan |
|---|---|---|
| `PROVIDER` | ADO.NET sağlayıcısı | `MySqlConnector` (`[SQLITE]` bölümünde `Microsoft.Data.Sqlite`, `[PGSQL]`: `Npgsql`) |
| `HOST` | Sunucu adresi (SQLite kullanmaz) | `localhost` |
| `PORT` | Port (SQLite kullanmaz) | `3306`, `[PGSQL]`: `5432` |
| `USER` | Kullanıcı | `""` |
| `PASSWORD` | Şifre (SQLite'ta dosya şifresi) | `""` |
| `DATABASE` | Veritabanı adı; SQLite'ta dosya yolu | `""` |
| `KEEPALIVE` | Bağlantıyı canlı tut (60 sn keepalive) | `0` |
| `CONNECTTIMEOUT` | Bağlanma zaman aşımı (saniye) | `30` (PostgreSQL: en çok 1024) |
| `READTIMEOUT` | Komut zaman aşımı (saniye) | `30` |
| `WRITETIMEOUT` | Yazma zaman aşımı (saniye). Okunur ama etkisi yok: bağlantı dizesine eklenmez. | `30` |
| `USETHREAD` | Sorguları ayrı bir thread'de çalıştır | `0` |
| `AUTOCONNECT` | Açılışta bağlan | `0` |
| `CHARSET` | MySQL oturum karakter seti, her bağlantıda `SET NAMES` ile gönderilir (aşağıya bakın) | `""` (utf8mb4) |

`CHARSET`: klasik Sphere veya PHP sitesinin latin1 oturumla yazdığı tablolar latin1 kolonda UTF-8 bayt tutar ve "ş" yerine "ÅŸ" okunur: `latin1` yazın. PostgreSQL'de yok sayılır (sunucu kolonun kodlamasından kendisi çevirir).

- **Not:** PostgreSQL: metin değerleri tek tırnakla yazılmalı (`'metin'`); `"metin"` orada kolon adıdır. `DB.ESCAPEDATA` `[PGSQL]` bağlantısında `'` işaretini ikiler.
- **Not:** SQLite: `DB.ESCAPEDATA` hem `'` hem `"` işaretini ikiler, değer iki tırnak türünde de güvenlidir; literal'in kullanmadığı tırnak işareti veride çift saklanır.
- **Örnek:**

```ini
[MYSQL default]
PROVIDER=MySqlConnector
HOST=localhost
PORT=3306
USER=sphere
PASSWORD=
DATABASE=sphere
KEEPALIVE=0
CONNECTTIMEOUT=30
READTIMEOUT=30
WRITETIMEOUT=30
USETHREAD=0
AUTOCONNECT=1
CHARSET=

[SQLITE local]
DATABASE=save/scriptdb.sqlite
AUTOCONNECT=1

[PGSQL stats]
HOST=localhost
PORT=5432
USER=sphere
PASSWORD=
DATABASE=sphere
AUTOCONNECT=1
```
