# SphereNet

> **.NET 10** üzerinde modern bir **Ultima Online sunucu emülatörü** — [Source-X](https://github.com/Sphereserver/Source-X) script uyumlu, altında yeniden inşa edilmiş çok çekirdekli bir motor.

🇬🇧 [README.md](README.md) · 📚 [docs/](docs/README.md) (mimari, [staff komutları](docs/STAFF_COMMANDS.md), [trigger'lar](docs/TRIGGERS.md), deploy, runbook) · 📜 [Changelog](CHANGELOG-TR.txt)

SphereNet, mevcut Sphere/Source-X `.scp` scriptlerini ve eski save verilerini değişiklik gerektirmeden çalıştırır; motoru ise çok çekirdekli donanım, büyük dünyalar ve canlı işletme için yeniden kurar. Çapraz platform (Windows/Linux/macOS), headless.

## Öne çıkanlar

| Alan | Ne sunar |
|---|---|
| **Scripting** | Tam Source-X `.scp` ayrıştırıcı: ifadeler, trigger'lar, `FOR`/`WHILE`/`DORAND`/`DOSWITCH`, defname çözümleme |
| **Savaş & Büyü** | Era seçilebilir savaş formülleri, 60+ Magery/Necromancy büyüsü + native Chivalry, gerçek field büyüleri, fizzle/kesinti, menzilli & fırlatma silahları |
| **Yetenek & Üretim** | Gain eğrili 30+ skill, tarif/kalite/malzeme rengi, madencilik/balıkçılık/oduncu, çıkrık & dokuma tezgâhı, taming |
| **NPC AI** | Monster/pet/healer/guard/vendor brain'leri, A\* pathfinding, aggro & ev-leash |
| **Dünya** | Housing (client-çizimli multiler, tabela gump'ı, lockdown/secure), gemiler (konuşma komutları, dry-dock), lonca & town, parti, hava, bitki büyümesi |
| **Ağ** | Tam UO login akışı, T2A→TOL paketleri, Blowfish/Twofish/Huffman |
| **Kalıcılık** | 4 kayıt formatı (`Text`→`BinaryGz`, ~%8–10 boyut), paralel shard'lı kayıt, arka plan kayıt modu, MySQL çoklu-DB |
| **Çok çekirdekli motor** | Seri apply'lı paralel tick hattı, sektör uykusu (boş dünya ≈ bedava), alan-bazlı delta view, bellek-eşlemeli haritalar |
| **İşletim** | SignalR web dashboard, Telnet yönetim konsolu, bot stres testleri (`STRESS`/`BOT`), SQLite kayıt/oynatma |

## Source-X'in Ötesinde

Klasik motorda olmayan yetenekler:

| | |
|---|---|
| **4 kayıt formatı + canlı geçiş** | `Text` (%100) / `TextGz` (~%15) / `Binary` (~%50) / `BinaryGz` (~%8–10); `.SAVEFORMAT BinaryGz 4` format + shard sayısını çalışırken taşır; `SAVESHARDS=2–16` paralel hash shard yazar; `SAVEBACKGROUND=1` yazımı ana döngüden çıkarır |
| **Çoklu MySQL veritabanı** | Aynı anda birden çok isimli `[MYSQL <isim>]` bağlantısı; scriptler `db.select <isim>` ile geçer |
| **Çok çekirdekli tick hattı** | Snapshot/Build fazları paralel, Apply seri & deterministik; hata durumunda otomatik tek-thread'e düşer |
| **Sektör uykusu** | Yalnızca çevrimiçi oyunculara yakın sektörler tick alır — 30 bin NPC'li boş dünya 0.1 ms. Uyuyan şey **karakter işi** (AI, regen, zehir); eşya son tarihleri, eşya nerede olursa olsun dünya düzeyindeki vade kuyruklarından **tam zamanında** çalışır ([sözleşme](docs/ARCHITECTURE.md#sectors-and-sector-sleeping)) |
| **Delta view** | Alan-bazlı değişiklik takibi (`DirtyFlag`) yalnızca değişeni gönderir, tam nesne tekrarı yok |
| **Bellek-eşlemeli haritalar** | MUL dosyalarını OS sayfalar (tam RAM yüklemeye göre ~200 MB tasarruf) |
| **NPC timer çarkı** | 256 slotlu çark NPC aksiyonlarını O(1) planlar; her tick tüm NPC'leri taramaz |
| **Web panel (SignalR)** | Canlı log akışı, CPU/RAM metrikleri, oyuncu listesi ve sunucu komutları tarayıcıda (port 9999) |
| **Bot stres testi** | Aynı process'te TCP botları gerçek login akışını tamamlayıp oynar: `.bot spawn 100` / `.botmenu`, telnet `BOT`/`STRESS` |
| **Kayıt & oynatma** | GM soruşturmaları ve debug için SQLite tabanlı hareket/durum kaydı |

## Performans

### Güncel ölçüm (2026-10-12)

`SphereNet.LoadTest` ile ölçüldü (repo içinde, tekrarlanabilir): ayrı bir süreçteki gerçek
TCP bot istemcileri normal akışla giriş yapar, yürür (~450 ms'de bir adım), konuşur ve
dclick yapar; ortam **2.000 dolaşan NPC ve 4.000 yer eşyası** olan sentetik bir kasabadır,
botların hepsi **kümelenmiştir** (her bot diğerlerini görür) ve her 45 sn'lik pencerenin
ortasında bir dünya kaydı alınır. Makine: AMD Ryzen 7 5700X3D (8C/16T), Windows 11, sıradan
yoğun bir masaüstü; 4 multicore worker. *Adım cevabı*, botun yürüme isteğinden sunucu
cevabına kadar geçen süredir ve bot tarafında ölçülür.

| Bot | Tick | Tick p50 / p95 | Adım cevabı p50 / p95 | Bellek ayırma |
|---|---|---|---|---|
| 50 | 100 ms (varsayılan) | 6,1 / 12,1 ms | 1,4 / 5,0 ms | 6,8 MB/sn |
| 200 | 100 ms (varsayılan) | 10,7 / 29,5 ms | 3,8 / 15,2 ms | 21 MB/sn |
| 50 | 50 ms | 2,8 / 10,0 ms | 1,5 / 5,5 ms | 6,8 MB/sn |
| 200 | 50 ms | 4,8 / 17,3 ms | 3,7 / 14,1 ms | 21 MB/sn |

- **Alım modeli** (`AsyncReceive`): tamamlanma tabanlı alım (varsayılan) ile yoklamalı yol
  (`AsyncReceive=0`, tur başına tek `Socket.Select`) aynı senaryoda — adım cevabı p50
  50 botta **1,4 / 4,3 ms**, 200 botta **3,8 / 6,8 ms**; tick süresi aynı.
- **Tick süresi**: `ServerTickMs=50` rahatça sığar (200 bot: 50 ms bütçede p95 17 ms) ve
  saniye başına yaklaşık aynı CPU'yu harcar, çünkü her tick işin yarısını yapar. Tick'te
  kontrol edilen zamanlayıcıları (vuruş, büyü) 100 ms yerine 50 ms hassasiyetle tetikler;
  NPC eylemleri 100 ms'lik zamanlayıcı çarkında, oyuncu adımları her döngü turunda
  işlendiği için onlar değişmez. Varsayılan 100 ms kalır (Source-X `MSECS_PER_TICK`).
- **Sınır**: **tek ekranda** 500 bot bu makineyi her ayarda aşırı yükler (tick p95 ~90 ms,
  adım cevabı p95 ~0,6 sn): her adım 499 izleyiciye gider. Aynı sayı haritaya dağıldığında
  çok daha ucuzdur — maliyeti belirleyen, kaç oyuncunun birbirini gördüğüdür.
- **Gerçek dünya**: canlı bir shard'ın kopyası (86.055 eşya, 26.767 karakter, 10.048
  spawner, Scripts-X paketi) şehirlere dağılmış 200 botla, giriş patlamasından sonra hiç
  yavaş tick ve duraklama olmadan çalıştı; 90 sn'lik profilde script yorumlayıcısı sunucu
  süresinin %0,1'inin altındaydı. 200 eşzamanlı giriş tek seferlik ~1,3 sn'lik bir duraklama
  yarattı.
- **Soak** (2026-10-14): aynı canlı shard kopyasında `Soak` senaryosunun 100 botu (aynı
  process'te; yürüme, dövüş, büyü, loot, vendor alışverişi, takas) 3 saat: tick p50
  0,23-0,35 ms, p99 2,2-4,1 ms, en kötü tick 40 ms; 12 dünya kaydı (her biri 0,45-0,72 sn
  senkron duraklama), 13 dünya denetiminde hiç anomali yok; working set 40 dakikalık
  ısınmadan sonra ~1 GB'ta yatay (koşu boyunca -7,7 MB/saat); ortalama 0,13 çekirdek.

Baskın maliyet, popülasyon veya istemci sayısı değil **aynı anda aktif olan AI'dır**. Uyuyan
sektör tick maliyeti taşımaz; uyanık sektör ise tuttuğu kadar değil **vadesi gelen kadar**
maliyet çıkarır — eşya son tarihleri, her tick her eşyayı gezerek değil, vade sıralı
kuyruklardan bulunur. Ayrı bir makinede ölçüldüğünde 500 oyuncu, 50.000 NPC ve 300.000 yer
eşyasıyla dünya tick'i şehirlerde kümeli oyuncularda 1,5 ms p50, haritaya dağınık
oyuncularda 11,9 ms p50; koşullar, yöntem ve timer sözleşmesi
[ARCHITECTURE](docs/ARCHITECTURE.md#sectors-and-sector-sleeping) içinde.

## Hızlı başlangıç

```bash
git clone https://github.com/Yunusolcay/sphereNet.git
cd sphereNet
dotnet build
dotnet run --project src/SphereNet.Server   # headless (web panel için SphereNet.Host)
```

`config/sphere.ini` düzenleyin: `MULFILES` UO istemci verinize, scriptler `SCPFILES` altına. Önemli anahtarlar: `SAVEFORMAT` (`Text`…`BinaryGz`), `SAVESHARDS` (0–16), `SAVEBACKGROUND`, `[MYSQL <isim>]` / `[PGSQL <isim>]` blokları. Tüm anahtarlar [docs/CONFIG-TR.md](docs/CONFIG-TR.md) içinde açıklanır ([İngilizce](docs/CONFIG-EN.md)).

Portlar: **2593** UO istemci · **2594** Telnet yönetim · **2595** HTTP durum · **9999** web panel (`ADMINPANELPORT`).

## Proje yapısı

```
src/
├── SphereNet.Core/          # Tipler, enum'lar, yapılandırma
├── SphereNet.Network/       # UO protokol, TCP, şifreleme
├── SphereNet.Scripting/     # .scp ayrıştırıcı, ifadeler, yürütme
├── SphereNet.Game/          # Oyun mantığı (AI, savaş, büyü, skill, dünya…)
├── SphereNet.MapData/       # MUL/UOP okuyucular
├── SphereNet.Persistence/   # Save/load, eski veri import'u
├── SphereNet.Panel/ + Host/ # Web dashboard / başlatıcı
├── SphereNet.Server/        # Sunucu giriş noktası
└── SphereNet.Tests/         # otomatik test paketi (her commit'te yeşil)
```

## Yol haritası

Bushido/Ninjitsu/Mysticism özgün mekanikleri · daha zengin NPC AI (sürü taktikleri) · genişletilmiş web panel (canlı harita, inceleyici) · artımlı kayıt.

## Katkı

Issue ve PR'ler memnuniyetle — değişiklikleri odaklı tutun, testle kapsayın ve göndermeden önce `dotnet build && dotnet test` çalıştırın.

**Teşekkürler:** [Source-X](https://github.com/Sphereserver/Source-X) (davranış referansı) · [ServUO](https://github.com/ServUO/ServUO) · [Ultima Online](https://uo.com/) — Origin/EA.
**Lisans:** açık kaynak — [LICENSE](LICENSE).
