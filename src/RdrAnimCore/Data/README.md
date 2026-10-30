# Template data (not in git)

`RdrAnimCore.csproj` embeds four files from this folder. They are made from the games' files, so they are not
published; put your own here before building:

| File | What |
| --- | --- |
| `carrier_face.bin` | an RDR 2 facial clip dictionary (`.ycd`, raw, with its RSC8 header: resource version 0x13A), used as the template the tool patches |
| `carrier.bin` | an RDR 2 body clip dictionary (same format), the template for body animations |
| `rdr_skeleton.json` | the RDR 2 MP ped skeleton (bones, parents, rest pose) |
| `gta_skeleton.json` | the GTA V MP ped skeleton (same layout) |

A clip dictionary extracted without its RSC8 header (resource version 0) crashes the game once patched: extract it
raw.
