# SphereNet 9.x Source-X Parite Yol Haritası

Bu doküman, güncel kod okumasına göre SphereNet'i Source-X paritesinde 9.0 / 9.5 seviyesine taşımak için izlenecek teknik planı tutar. Ölçüm manuel hissiyata değil, kod yüzeyine ve test guardrail'lerine dayanmalıdır.

Son güncelleme: 2026-06-30

Doğrulanan test durumu: `dotnet test .\src\SphereNet.Tests\SphereNet.Tests.csproj --nologo` -> 1011/1011 başarılı.

## Güncel Skor

| Kategori | Source-X | SphereNet |
|---|---:|---:|
| Combat | 10 | 8.4 |
| Skills / skill gain / caps | 10 | 8.4 |
| Magery / spells | 10 | 7.9 |
| NPC AI | 10 | 7.5 |
| Item / container / equip / use / drag-drop | 10 | 7.8 |
| Death / corpse / loot / resurrection | 10 | 8.5 |
| Notoriety / crime / karma / fame / murder | 10 | 8.4 |
| Spawn / region / sector / world tick | 10 | 7.7 |
| Housing / ships / multis | 10 | 7.3 |
| Trade / vendor / banker / stable | 10 | 7.5 |
| Crafting / gathering / resources | 10 | 7.4 |
| Speech / commands | 10 | 7.7 |
| Scripting / triggers genel | 10 | 7.9 |
| `SERV.*` coverage | 10 | 7.6 |
| Arbitrary verb / function fallback | 10 | 8.2 |
| `RETURN/ARGS/ARGN/ARGO/LOCAL` paritesi | 10 | 8.3 |
| Network / client protocol | 10 | 7.6 |
| Persistence / save-load | 10 | 7.7 |
| Guild / party / chat / stones | 10 | 5.8 |

Genel güncel parite: yaklaşık 7.8 / 10.

## Önemli Düzeltme

`SERV.*`, arbitrary verb ve `RETURN/ARGS/ARGN/ARGO/LOCAL` altyapısı eksik kabul edilmemelidir. Güncel kodda:

- `ScriptInterpreter` bilinmeyen komutu Source-X tarzı fonksiyon çağrısına düşürür.
- `SRC.` prefix'i property, verb, script-command ve fonksiyon fallback akışını destekler.
- `RETURN`, `ARGS`, `ARGN1/2/3`, `ARGO`, `ACT`, `LOCAL`, `REFn`, `UID.*` ve `SERV.*` önemli ölçüde çalışır.
- Kalan fark, temel mekanizma yokluğu değil; Source-X `CServer::r_Verb` ve obje/client verb yüzeyindeki uzun kuyruktur.

## 9.0 Hedefi

9.0 için en yüksek getirili işler:

1. `SERV.*` uzun kuyruğunu kapat:
   `VARLIST`, `PRINTLISTS`, `CLEARLISTS`, `EXPORT`, `IMPORT`, `RESTORE`, `SAVESTATICS`, `SECURE`, `SHRINKMEM`, `INFORMATION`, `LOAD`, `GARBAGE`, `BLOCKIP`, `UNBLOCKIP`, `RESPAWN`, `RESTOCK`.
2. `SERV.ACCOUNT.n` indeksli erişimi gerçek account sırasına bağla.
3. `DEFMSG` setter'ını sadece loglayan stub olmaktan çıkar; runtime override ve save/load desteği ver.
4. `TRYP`, `TRY`, `TRYSRC`, `TRYSRV` için Source-X davranış testlerini genişlet.
5. `TIMERF/TIMERFMS` kapsamını char/item/function edge-case'leriyle testle.
6. Item/container edge-case'lerini kapat: nested limit, stack split/merge, drop-on-NPC, equip layer conflict, decay/timer trigger sırası.
7. NPC AI kararlarını derinleştir: flee/fight/cast/heal/loot/guard/pet/vendor davranışlarını trigger arg ve RETURN etkisiyle testle.

## 9.5 Hedefi

9.5 için 9.0 üstüne şu uzun kuyruk gerekir:

1. Housing ve multis:
   transfer, ban/eject, secure/lockdown edge-case, house gump roundtrip, decay ve custom housing save/load doğrulaması.
2. Ships:
   multi collision, plank/key/access, region crossing, blocked movement, `PacketBoatSmoothMove` bileşen listesi paritesi.
3. World/admin ops:
   `EXPORT/IMPORT/RESTORE/SAVESTATICS`, global `RESPAWN/RESTOCK`, sector sleep/wakeup ve save/restore drill.
4. Persistence:
   timers, memories, spawner state, house/ship state, notoriety/murder timers ve script-facing fields için save-load regresyonları.
5. Guild/party/chat/stones:
   guild stone yönetimi, alliance/member rank edge-case'leri, conference/global chat uzun kuyrukları.
6. Packet parity:
   opcode matrix fixture corpus, era-specific packet seçimleri, region/weather/light transition sequence testleri.

## Ölçüm Disiplini

Her yeni parity dalgası en az bir guardrail ile kapanmalı:

- Source-X verb/property/trigger yüzeyi için `Implemented`, `Partial`, `Stub`, `Missing`, `NotApplicable` matrisi.
- Kod yüzeyi testi: `TryExecuteCommand`, `TryGetProperty`, `TrySetProperty`, `TriggerDispatcher`, `ScriptInterpreter`.
- Davranış testi: ilgili packet, trigger arg/return mutasyonu ve save-load sonucu.
- Doküman güncellemesi: `PARITY.md`, `STUB_INVENTORY_TR.md`, `TRIGGERS.md` ve gerekiyorsa paket rehberi.

## Öncelikli Sıra

1. Parite matrisi otomasyonu.
2. `SERV.*` ve server/admin verb uzun kuyruğu.
3. Trigger arg/return/order detay testleri.
4. Item/container ve NPC AI edge-case'leri.
5. Housing/ships/world ops/persistence uzun kuyruğu.
