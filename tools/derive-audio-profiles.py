"""Developer-only numeric audio recipe compiler. Nothing here runs in the launcher.

The reference parser must parse both versions without skipped bytes or errors.
Profiles contain bounded field edits and hashes, never compressed media payloads.
"""
import argparse, base64, hashlib, importlib.util, json, struct
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument('--dump', type=Path, required=True)
p.add_argument('--cache', type=Path, required=True)
p.add_argument('--assets', required=True)
p.add_argument('--reference', type=Path, required=True)
p.add_argument('--work', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
a = p.parse_args(); a.work.mkdir(parents=True, exist_ok=True)
u32 = lambda b, o: struct.unpack_from('<I', b, o)[0]
sha = lambda b: hashlib.sha256(b).hexdigest().upper()
def manifest(folder, ident):
    raw = (a.cache/'inputs'/folder/(ident.lower()+'.json')).read_bytes()
    assert sha(raw) == ident.upper()
    return json.loads(raw)

assets = manifest('asset-manifests', a.assets)
required = set(); origins = []
for bid in assets['BatchIds']:
    batch = manifest('asset-batches', bid)
    selected = [e for e in batch['Assets'] if base64.b64decode(e['Entry'])[64:68] == b'knbs']
    if not selected: continue
    with (a.cache/'inputs/asset-packs'/(batch['PackId'].lower()+'.pack')).open('rb') as f:
        for e in selected:
            f.seek(e['Offset']); raw = f.read(e['Length']); assert sha(raw) == e['Sha256']
            at = 80 + u32(raw, 28)*24
            assert u32(raw,32)==2
            blocks=[]
            for i in range(2):
                size, _, section, offset = struct.unpack_from('<IhhQ',raw,at+i*16)
                assert section==1
                start=u32(raw,60)+offset; blocks.append(raw[start:start+size])
            assert len(blocks[0])==64 and len(blocks[1])%4==0
            ids = [v[0] for v in struct.iter_unpack('<I',blocks[1])]
            name=e['Name'].replace('\\','/').split('/')[-1].rsplit('.',1)[0].lower()
            hashed=2166136261
            for char in name.encode('utf8'): hashed=((hashed*16777619)&0xffffffff)^char
            assert hashed==u32(blocks[0],56) and hashed in ids
            required.update(ids); origins.append(dict(File=batch['SourceFile'],Item=e['Item'],Ids=ids))

# Instrument the reviewed converter to return its numeric edit log as well as
# its full-parser proof. The original reference remains untouched.
source=a.reference.read_text()
needle='return dict(id=bankid,source='
assert source.count(needle)==1
source=source.replace(needle,"return dict(recipe=[dict(Offset=at,Before=raw[at:at+old].hex(),After=new.hex(),Reason=reason) for at,old,new,reason in sorted(edits)],id=bankid,source=")
scope={'__file__':str(a.reference),'__name__':'reference_audio_converter'}
exec(compile(source,str(a.reference),'exec'),scope)
spec=importlib.util.spec_from_file_location('reference_wem',a.reference.parent/'Wem112.py')
wem=importlib.util.module_from_spec(spec); spec.loader.exec_module(wem)
def object_count(raw):
    at=0; count=0
    while at<len(raw):
        size=u32(raw,at+4)
        if raw[at:at+4]==b'HIRC': count+=u32(raw,at+8)
        at+=8+size
    assert at==len(raw)
    return count
recipes=[]; found=set(); media_unsupported=[]
for language, package in [('SFX','soundbank'),('SFX','soundstream'),('English(US)','soundvoice')]:
    path=a.dump/'sound/Durango'/language/(package+'.pck')
    with path.open('rb') as f:
        head=f.read(28); length=u32(head,4)+8; f.seek(0); tables=f.read(length)
        at=28+u32(head,12); count=u32(tables,at)
        for i in range(count):
            ident, block, size, offset, lang = struct.unpack_from('<5I',tables,at+4+i*20)
            if ident not in required: continue
            f.seek(block*offset); raw=f.read(size); assert len(raw)==size
            src=a.work/(sha(raw)+'.source.bnk'); dst=a.work/(sha(raw)+'.native.bnk')
            src.write_bytes(raw); result=scope['convert'](src,dst)
            assert result['id']==ident and result['parsed_without_errors']
            assert object_count(raw)==object_count(dst.read_bytes())
            try:
                (a.work/(sha(raw)+'.expected.bnk')).write_bytes(wem.bank(dst.read_bytes()))
            except AssertionError as error:
                media_unsupported.append(dict(Id=ident,SourceSha256=sha(raw),Error=str(error)))
            for e in result['recipe']: assert len(e['Before'])<=10 and len(e['After'])<=16
            recipes.append(dict(Id=ident,InputSha256=sha(raw),OutputSha256=sha(dst.read_bytes()),InputBytes=size,
                                OutputBytes=dst.stat().st_size,Objects=result['objects'],HircObjects=object_count(raw),Edits=result['recipe']))
            found.add(ident); print(json.dumps(dict(bank=ident,bytes=size,edits=len(result['recipe']))),flush=True)
assert found==required,('missing banks',required-found)
unique={r['InputSha256']:r for r in recipes}
a.output.parent.mkdir(parents=True,exist_ok=True)
a.output.write_text(json.dumps(dict(Format=1,SourceVersion=118,NativeVersion=112,Recipes=sorted(unique.values(),key=lambda r:r['InputSha256'])),separators=(',',':')))
(a.work/'proof.json').write_text(json.dumps(dict(Required=sorted(required),Origins=origins,Recipes=len(unique),MediaUnsupported=media_unsupported,ProfileSha256=sha(a.output.read_bytes())),indent=2))
print(json.dumps(dict(required=len(required),recipes=len(unique),profile_bytes=a.output.stat().st_size)),flush=True)
