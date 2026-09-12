# Büyü matrisi — hangi büyü gerçekten çalışıyor

Bu dosya, "okul X enum-only" gibi **toplu etiketlerin yerini alır** (PLAN-601).
Sayılar elle yazılmadı: canlı script paketinden (`C:\sphereNetServer\scripts`)
ölçüldü ve `SpellCoverageGuardrailTests` tarafından sabitlendi — yani bu belge
paketten sessizce ayrışamaz. Pakete erişilemeyen bir ortamda test temiz atlar.

## Sınıflandırma nasıl yapılıyor

Motorun kendi yüklemi kullanıldı (`SpellEngine.IsInertSchoolSpell`). Bir büyü
şu durumlardan **herhangi biri** geçerliyse çalışır:

| Yol | Anlamı |
|---|---|
| **Yerli kimlik alanı** | id < 201 (Magery / Necromancy) — `ApplySpecificSpell` içinde id'ye göre dağıtılır |
| **Yerli el yazması** | `HasNativeSchoolHandler` / `HasNativeCustomHandler` listesindeki okul ve özel büyüler |
| **Bayrak sürümlü** | Paket def'i `SPELLFLAG_DAM/HARM/HEAL/BLESS/CURSE/FIELD/SUMMON/AREA` taşıyor — genel dallar işi görür |
| **Script'li** | Paket `ON=@...` aşaması yazmış — davranış paketin |

Hiçbiri geçerli değilse büyü **reddedilir**: `CastStart` daha hiçbir bedel
alınmadan `-1` döner ve oyuncuya "That spell is not supported yet." denir.
Dalganın kabul ölçütü tam olarak budur — desteklenmeyen özellik başarı dönüp
sessizce hiçbir şey yapmaz.

## Canlı paket ölçümü

Paket **168 büyü** tanımlıyor; **138'i çalışıyor, 30'u reddediliyor.**

| Okul | Tanımlı | Çalışan | Reddedilen |
|---|---:|---:|---:|
| Magery | 64 | 64 | 0 |
| Necromancy | 17 | 17 | 0 |
| Chivalry | 10 | 8 | 2 |
| Bushido | 6 | **0** | 6 |
| Ninjitsu | 8 | **0** | 8 |
| Spellweaving | 16 | 14 | 2 |
| Mysticism | 16 | 11 | 5 |
| Bard Masteries | 6 | **0** | 6 |
| Skill Masteries | **0** | 0 | 0 |
| Sphere custom (1000+) | 24 | 23 | 1 |

## Reddedilen 30 büyü

| Okul | Büyüler |
|---|---|
| Chivalry | ConsecrateWeapon (203), EnemyOfOne (206) |
| Bushido | HonorableExecution, Confidence, Evasion, CounterAttack, LightningStrike, MomentumStrike (401-406) |
| Ninjitsu | FocusAttack, DeathStrike, AnimalForm, KiAttack, SurpriseAttack, Backstab, Shadowjump, MirrorImage (501-508) |
| Spellweaving | ArcaneCircle (601), DryadAllure (612) |
| Mysticism | HealingStone (679), Enchant (681), SpellTrigger (686), MassSleep (687), CleansingWinds (688) |
| Bard Masteries | Inspire, Invigorate, Resilience, Perseverance, Tribulation, Despair (701-706) |
| Sphere custom | EnchantItem (1022) |

## Eski toplu etiket neden yanlıştı

`docs/PORT_DURUM_RAPORU_TR.md` şunu diyordu:
*"Bushido/Ninjitsu/Mysticism/Spellweaving enum-only"*. Ölçüm bunu **dört ayrı
noktada** çürütüyor:

1. **Spellweaving enum-only değil** — 16 tanımlı büyünün **14'ü** çalışıyor.
2. **Mysticism enum-only değil** — 16'nın **11'i** çalışıyor.
3. **Bard Masteries hiç anılmıyor** ama gerçekten ölü: **0/6**.
4. **Chivalry "etkili" sayılıyordu** ama iki büyüsü reddediliyor (203, 206).

Doğru olan iki yarısı: **Bushido (0/6) ve Ninjitsu (0/8) gerçekten ölü.**

## Skill Masteries — enum paketin önünde

`SpellType` 39 Skill Mastery üyesi (707+) taşıyor ve **canlı paket bunların
hiçbirini tanımlamıyor.** Yani bu 39 giriş bir eksiklik değil, erişilemez bir
kimlik alanı: paket bir tanım yazmadıkça hiçbir oyuncu seçemez. Matriste
"tanımlı 0" satırı bunu açıkça gösteriyor.

## Bu belge nasıl güncellenir

Sayıları elle düzenlemeyin. `SpellCoverageGuardrailTests` kırmızıya döndüyse
paket değişmiş demektir: testin yazdırdığı satırları (`defined/castable/refused`
ve id listeleri) alın, buradaki tabloları ve testteki `Expected` dizisini birlikte
güncelleyin.
