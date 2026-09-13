# Nesne üretim sözleşmesi

PLAN-101: *"Tek nesne oluşturma/kopyalama sözleşmesi: NEWITEM, NEWNPC, NEWDUPE,
karakter DUPE, spawn, template, vendor ve stack bölme girişlerinin ortak ve
farklı davranışlarını tabloya dök."*

"Yeni bir nesne belirir" cümlesinin dokuz ayrı kapısı var. Hepsi farklı
dosyalarda, farklı dalgalarda yazıldı ve paylaştıkları hata sessiz: bir kapı bir
alanı kopyalar, yanındaki unutur, hiçbir şey söylemez. Karakterin `DUPE` fiili
bir zamanlar elle seçilmiş bir avuç alanı kopyalıyor ve çıplak bir karakter
üretiyordu; `NEWDUPE` ise hepsini taşıyordu. `Character.cs`'teki CHV_DUPE yorumu
o olayı hâlâ kaydediyor.

---

## İki aile

| Aile | Kapılar | Sorulacak soru |
|---|---|---|
| **Kaynaktan kopyalayan** | eşya `DUPE`, karakter `DUPE`, `NEWDUPE`, stack bölme | Hangi alanlar taşınıyor? |
| **Tanımdan üreten** | `NEWITEM`, `NEWNPC`, spawn, template, vendor restock | Hangi ilklendirme adımları koşuyor? |

İki aile farklı şeyler ölçer, o yüzden aynı tabloya konmazlar.

---

## Kaynaktan kopyalayanlar — ölçüm

Karşılaştırma elle tutulan bir listeye değil **yansımaya** dayanıyor:
`Item`'ın okunabilir her property'si, tamamen doldurulmuş bir kaynakla kopyası
arasında karşılaştırılıyor. Sonradan eklenen ve bir kapının kopyalayıp
diğerinin unuttuğu bir alan, kimse listeyi güncellemeden ortaya çıkar.

`Item` **63 karşılaştırılabilir property** sunuyor; 21 ad dışarıda:

| Dışlanan küme | Neden | Örnek |
|---|---|---|
| Kimlik | Kopya ikinci bir nesne olmasaydı zaten kopya olmazdı | `Uid`, `Serial`, `UidRef`, `Uuid` |
| Yerleşim | Referans yerleştirmeyi çağırana bırakır (CIV_DUPE) | `Position`, `Container`, `Contents` |
| Türetilmiş | Taşınan alanlardan yeniden hesaplanır | `TotalWeightTenths`, `DirtyFlags` |

**Sonuç: 63 property'nin tamamında eşya `DUPE` ve stack bölme birbiriyle ve
kaynakla aynı.** Bu bir tahmin değil, her koşuda yeniden ölçülen bir sonuç.

### Bilinçli tek fark

Kap kopyalama: `DUPE` bir kabın **içeriğini** de kopyalar, her biri kendi yeni
uid'siyle (`CItemContainer::DupeCopy`, CItemContainer.cpp:830). Stack bölme tek
nesne üretir, taşıyacak ağacı yoktur. Bu fark referanstan geliyor ve kaza
türünden ayırt edilebilsin diye ayrı bir testle kaydedildi.

Spawner kopyası da aynı sözleşmenin parçası: `CCSpawn::Copy` (CCSpawn.cpp:1272)
**yalnız yapılandırmayı** taşır, üretilmiş çocukları değil. Çocukları da taşıyan
bir kopya iki spawner'a aynı yaratıkları verirdi; hiç bileşen kurmayan bir kopya
doğru görünüp hiçbir şey yapmazdı.

---

## Bulgu — `NEWDUPE` karakterde DUPE'tan ayrışıyordu

Kapıları yan yana koymanın karşılığı bu oldu.

Referansta `SSV_NEWDUPE` **kendi başına kopyalamaz**: `CScript("DUPE")` kurup
nesnenin kendi fiilini çağırır (CScriptObj.cpp:1311).

```cpp
g_World.m_uidNew = uid;
CScript script("DUPE");
script.CopyParseState(s);
bool bRc = pObj->r_Verb(script, pSrc);
```

Kritik ayrıntı `CopyParseState`'te: yalnızca ayrıştırma bayraklarını, dosya
indeksini ve satır numarasını kopyalar (CScript.cpp:458) — **argümanı değil**.
Yani kurulan script'in argümanı yok, `CHV_DUPE` içindeki `s.GetArgVal()` sıfır
döner ve

```cpp
pChar->DupeFrom(this, s.GetArgVal() < 1 ? true : false);   // CChar.cpp:4545
```

`fNewbieItems = true` verir. **Referansta `NEWDUPE <uid>` bir karakteri
kopyalarken ekipmanını ATTR_NEWBIE işaretler.**

SphereNet'in `HandleNewDupe`'u doğrudan `CreateDupe(_world)` çağırıyordu ve
varsayılan `false`'tu. Üstelik metodun kendi doküman yorumu *"DUPE fiili
argümanından seçer; NEWDUPE seçmez"* diyerek yanlış gerekçeyi kaydediyordu.

**Oyun içi etkisi kozmetik değil:** ATTR_NEWBIE eşya ölümde cesede düşmez,
karakterle kalır. Bir script NEWDUPE ile ürettiği NPC'nin ölümünde referansta
ekipmanı korunurken burada yere düşüyordu.

Düzeltme çağrı yerinde: `CreateDupe(_world, newbieItems: true)`, zinciri
gerekçesiyle birlikte anan bir yorumla. Metodun varsayılanı değiştirilmedi —
`DUPE` fiili zaten argümanından açıkça seçiyor.

---

## Tanımdan üretenler

Bunlar kaynaktan kopyalamaz, bir tanımdan kurar; karşılaştırma ekseni hangi
ilklendirme adımlarının koştuğudur.

| Kapı | Giriş | Not |
|---|---|---|
| `NEWITEM` | `Character.cs` verb + `ScriptInterpreter` | Çıplak biçim ayrıca çağıranın `ACT`'ini kurar (CScriptObj.cpp:1383); `SERV.` biçimi kurmaz |
| `NEWNPC` | `Character.cs` verb + `ScriptInterpreter` | — |
| spawn | `SpawnComponents` `CreateCharacter`/`CreateItem` | Üyelik, `@Create` zinciri, yerleşim |
| template | `TemplateEngine.BuildTemplate` | Tarif açılımı; kap içeriğiyle birlikte |
| vendor restock | `TradeEngine` | `TAG.VENDORINV`'den |

---

## Açık kalem — çıplak `NEWDUPE` ve `ACT`

Referans, NEWDUPE'tan sonra `this != &g_Serv` ise çağıranın `m_Act_UID`'sini
kuruyor (CScriptObj.cpp:1311 civarı). Bu, `NEWITEM`'da zaten karşılanmış ve
kodda yorumla anılmış bir sözleşme; `NEWDUPE` tarafında **karşılanmıyor**.

Bu turda uygulanmadı çünkü referansın `m_uidNew`'ü DUPE fiilinden **önce
kaynağın uid'sine** kuruyor olması okumayı belirsizleştiriyor: ACT'in kaynağı mı
kopyayı mı gösterdiği ayrıca doğrulanmalı. Tahmine dayalı bir uygulama, bu
dalgada düzeltilen hatanın aynısı olurdu. Kayda geçirildi.

---

## Koruma

`ObjectCreationContractTests` (8):

1. Eşya `DUPE`'u 63 property'de kaynakla karşılaştırır.
2. Stack bölmeyi aynı şekilde karşılaştırır; tek ayrıcalık `Amount`.
3. **İki kapıyı birbiriyle** karşılaştırır — asıl risk bir kapının kaynaktan
   değil, iki kapının birbirinden ayrılmasıdır; o zaman aynı script hangi
   kapıdan geçtiğine göre farklı nesne üretir.
4. Kap içeriği farkını bilinçli fark olarak kaydeder.
5. Spawner kopyasının yapılandırmayı taşıyıp çocukları taşımadığını ölçer.
6. Karşılaştırmanın gerçekten 30'dan fazla alana baktığını doğrular — yüzey
   çökerse testler ölçüm yapmadan yeşil kalırdı.
7. Newbie sözleşmesini davranış olarak ölçer.
8. `NEWDUPE` çağrı yerinin `newbieItems: true` geçtiğini sabitler.

Geri-alma sondajı: düzeltme geri alınınca 8 testin biri kırmızıya dönüyor.
