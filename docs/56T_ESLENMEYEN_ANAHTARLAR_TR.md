# 56T eşlenmeyen anahtarlar

PLAN-106: *"56T'nin 17 eşlenmeyen anahtar türünü sınıflandır.
KILLSPLAYER/KILLSNPC, guild alanları, bölge tag'leri, custom skill ve gemi
alanlarını ayrı ele al; **her SAVE.\* kaydını otomatik motor hatası sayma.**"*

Gerçek 56T kaydı (`C:\56T\save`, 76.359 eşya / 4.187 karakter) üzerinde ölçüldü.

---

## Sonuç

| Yapılandırma | Eşlenmeyen anahtar türü |
|---|---:|
| İlk sayım (2026-09) | 17 |
| Skill adı dilimi sonrası | 15 |
| **Bugün, gerçek sunucu yapılandırması** | **0** |

Ölçüm `Sphere56TSaveCompatTests.WithTheScriptPackLoaded_ThePacksOwnSkillNamesAreRead`
içinde ve artık `Assert.Empty(unhandled)` ile **sabitlenmiş**.

---

## Ölçümün nasıl yapılmaması gerektiği

Bu iş iki kez yanlış ölçüldü; ikisi de aynı hatanın biçimleri.

**1. Kaynak taraması.** Yükleyicinin kaynağındaki string literal'leri toplayıp
kalanı "eşlenmedi" saymak, paketteki **her skill'i** eksik gösteriyor — bir
skill'in adı literal'den değil script paketinden geliyor. (Aynı hata port
raporunun sayılarını da bozmuştu, bkz. [İŞ-50].)

**2. Çözücüsüz yükleme.** Paketi yükleyip kaydı okumak yetmiyor: sunucunun
kurduğu `ResolveItemDef` / `ResolveItemDefFullIndex` / `ApplyCharDefFromName`
çözücüleri kurulmazsa **76.359 eşyanın hepsi `BaseId=0`** geliyor, hiçbir tip
çözülmüyor ve ölçüm 9 anahtar bildiriyor. Bu 9'u "motor hatası" diye
raporlamak üzereydim; son kontrol kurtardı.

Doğru ölçüm, sunucunun çalıştığı yapılandırmayı birebir kuran mevcut test
harness'ıdır — o harness'ın kendi yorumu da bu tuzağı zaten söylüyordu.

---

## Anahtar aileleri ve nereye gittikleri

Çözücüsüz ölçümde parklanan 9 anahtar, çözücülerle birlikte sıfıra iniyor.
Hiçbiri "bilinmeyen anahtar" değildi; hepsi **tipi çözülmemiş nesnede** duruyordu:

| Aile | Anahtarlar | Ait olduğu tip | Neden parklanıyordu |
|---|---|---|---|
| Lonca taşı | `ALIGN`, `MEMBER`, `ABBREV`, `CHARTER0` | `t_stone_guild` | Başlık defname'i (`i_guild_stone`) ITEMDEF'e ulaşmayınca nesne `Normal` kalıyor |
| Harita | `PIN` | `t_map` | Aynı |
| Kitap | `BODY.0`–`BODY.3` | `t_book` | Aynı |

Yani kök neden tek: **kaydın başlık defname'i bir ITEMDEF'e çözülmeli.**
Çözülünce tip gelir, tip gelince anahtarın sahibi olan alt sistem onu okur.

PLAN-106'nın ayrı ele alınmasını istediği kümeler:

| Küme | Durum |
|---|---|
| `KILLSPLAYER` / `KILLSNPC` | Okunuyor |
| Guild alanları | Okunuyor (lonca taşı tipi çözülüyor) |
| Bölge tag'leri | Okunuyor — kale bölgesi adı, `OWNER` tag'i ve EVENTS listesiyle geri geliyor |
| Custom skill | Okunuyor — paketin kendi adları (`Sailormanship`, `Farming`) skill olarak çözülüyor |
| Gemi alanları | Okunuyor — gövde `t_ship` olarak, ambarı ve iki plankasıyla |

---

## Neden bir `SAVE.*` kaydı otomatik hata değil

Plan bunu özellikle uyarıyor ve doğru uyarıyor: parklanan bir anahtar
**kaybolmuyor**. Değer tag'de duruyor, script'ten okunabiliyor ve bir sonraki
kayıtta geri yazılıyor. Kaybolan şey **motor davranışı**, veri değil.

Bu yüzden sınıflandırmanın sorusu "bu anahtar tanınıyor mu" değil, *"bu anahtar
motorun ne yapmasını gerektiriyor ve o yapılıyor mu"*.

---

## Hibrit kural

56T desteği Source-X yapısının **yerine değil yanına** ekleniyor. Klasik
yazımlar Source-X yazımlarıyla birlikte okunuyor; bir 56T biçimini desteklemek
için Source-X biçimini değiştirmek ya da kaldırmak yok. İki soy da yüklenebilir
durumda kalmalı.

---

## Koruma

`Sphere56TSaveCompatTests.WithTheScriptPackLoaded_ThePacksOwnSkillNamesAreRead`
artık sıfırı sabitliyor. Yalnızca iki skill adının parklanmadığını iddia etmek
yeterli değildi — yirmi başka anahtar parklansa test yine geçerdi; bir ölçümün
sessizce ölçmeyi bırakma biçimi tam olarak budur.

Sondaj: `ResolveItemDefFullIndex` devre dışı bırakılınca lonca taşının dört
anahtarı (`ALIGN`, `MEMBER`, `ABBREV`, `CHARTER0`) parklanıyor ve sabitleme
kırmızıya dönüyor.

`C:\56T` yoksa test temiz atlıyor (bkz. [veri kapıları](VERI_KAPILARI_TR.md),
kaynak adı `56T scripts and save`).
