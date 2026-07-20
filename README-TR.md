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

## Performans

**2026-07-20'de güncel build üzerinde**, kod içi araçla (gerçek TCP bot istemcileri, tam üretim script paketi) mütevazı bir **5 vCPU VM, 12 GB RAM** üzerinde ölçüldü. Tick = 100 ms (saniyede 10, Source-X paritesi); botlar aynı process'te — sayılar kötümserdir.

| Senaryo | Ort. tick | p95 | Bütçe |
|---|---|---|---|
| 30.000 NPC, oyuncu yok | 0.1 ms | <2 ms | <%1 |
| 30.000 NPC + yürüyen 300 oyuncu | ~8 ms | ~13 ms | %8 |
| 2.000 saldırgan + savaşan 300 oyuncu | ~3.5 ms | ~7 ms | %4 |
| Gezinen 1.000 canlı istemci | ~2.5 ms | ~5 ms | %3 |
| 1.000 istemci + 2.000 saldırgan | ~15 ms | ~30 ms | %15 |
| 100 bin item + 50 bin NPC + 300 oyuncu, hepsi aktif | ~31 ms | ~58 ms | %31 |

- Login: 300 istemci 7.4 sn, 1.000 istemci 18.1 sn — sıfır hata. Soğuk açılış < 2 sn.
- Kayıt: 102.400 item + 50.440 karakter → **1.08 sn**, dünya çalışmaya devam ederken (BinaryGz, 3 shard, paralel yakalama).
- GC: bloklayan Gen2 her senaryoda 30 sn'lik pencere başına ≈ 0–1; RSS ~450–700 MB.

Baskın maliyet, popülasyon veya istemci sayısı değil **aynı anda aktif olan AI'dır** — uyuyan sektörler bedavadır.

## Hızlı başlangıç

```bash
git clone https://github.com/Yunusolcay/sphereNet.git
cd sphereNet
dotnet build
dotnet run --project src/SphereNet.Server   # headless (web panel için SphereNet.Host)
```

`config/sphere.ini` düzenleyin: `MULFILES` UO istemci verinize, scriptler `SCPFILES` altına. Önemli anahtarlar: `SAVEFORMAT` (`Text`…`BinaryGz`), `SAVESHARDS` (0–16), `SAVEBACKGROUND`, `[MYSQL <isim>]` blokları.

Portlar: **2593** UO istemci · **2594** Telnet yönetim · **2595** HTTP durum · **2596** web panel.

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
└── SphereNet.Tests/         # ~1.900 otomatik test (her commit'te yeşil)
```

## Yol haritası

Bushido/Ninjitsu/Mysticism özgün mekanikleri · daha zengin NPC AI (sürü taktikleri) · genişletilmiş web panel (canlı harita, inceleyici) · artımlı kayıt.

## Katkı

Issue ve PR'ler memnuniyetle — değişiklikleri odaklı tutun, testle kapsayın ve göndermeden önce `dotnet build && dotnet test` çalıştırın.

**Teşekkürler:** [Source-X](https://github.com/Sphereserver/Source-X) (davranış referansı) · [ServUO](https://github.com/ServUO/ServUO) · [Ultima Online](https://uo.com/) — Origin/EA.
**Lisans:** açık kaynak — [LICENSE](LICENSE).
