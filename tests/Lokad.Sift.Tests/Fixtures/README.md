# Segment compatibility fixtures

`segment-v3.pack` was produced by the unmodified Lokad.Sift 0.2.0 writer at commit
`436a7d8`, built in Release. It contains:

| Path | UTF-8 content |
| --- | --- |
| `src/a.txt` | `alpha one\n` |
| `src/b.txt` | `beta two\n` |
| `src/empty.txt` | empty |
| `src/unicode.txt` | `café alpha\n` |

The fixture includes the original 1044-byte stats region and its checksums.
Tests also derive the formerly supported 1032-byte variant by removing only the
Unicode totals and updating the directory/checksum metadata. When installing a
pack in a test corpus, only its header segment ID is changed to match that
corpus's manifest; region contents and their checksums remain unchanged.

`segment-v4.pack` contains the same documents, written by the actual published
Lokad.Sift 0.3.0 DLL extracted from its NuGet package. It retains populated
ASCII-folded path trigram regions, unlike new writers which retain empty wire
placeholders. SHA-256:
`B325B1742A418E6C7E09C0C8CADDE6EE23716346D413BF492F2C243EED05B896`.
Compatibility tests install it with only its segment ID adjusted, then exercise
validation, path/content search, updates, retained snapshots and compaction.
