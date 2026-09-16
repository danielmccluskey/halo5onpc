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
p.add_argument('--merge', type=Path, help='Preserve existing bank recipes when adding a campaign bundle')
p.add_argument('--source-gaps', type=Path, help='Reviewed missing source banks with exact tag hashes')
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
            if name=='sb_120_mus_campaign_campsite_return' and sha(raw)=='0920B2900D5D5D43B5805FDEA49482653A1072BBEC8D12206672492440D8736C':
                hashed=0x6de0d5f8  # Original bank identity, retained despite the tag filename.
            assert hashed==u32(blocks[0],56) and hashed in ids
            required.update(ids); origins.append(dict(File=batch['SourceFile'],Item=e['Item'],Ids=ids,Name=name,TagSha256=sha(raw)))

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

def preserved_media(path, raw):
    """Only plugin-owned convolution payloads qualify; other non-WEM data fails."""
    root=scope['parse'](path); convolution=set()
    for node in scope['walk'](root):
        if node.get_name()!='FxBaseInitialValues': continue
        effect=node.find1(name='fxID')
        if effect is None or effect.value()!=0x007F0003: continue
        media=node.find1(name='media')
        if media is not None:
            convolution.update(n.value() for n in scope['walk'](media) if n.get_name()=='sourceId')
    chunks={}; at=0
    while at<len(raw):
        size=u32(raw,at+4); chunks[raw[at:at+4]]=raw[at+8:at+8+size]; at+=8+size
    proofs=[]
    for ident,offset,size in struct.iter_unpack('<III',chunks.get(b'DIDX',b'')):
        data=chunks[b'DATA']; assert offset<=len(data) and size<=len(data)-offset
        payload=data[offset:offset+size]
        if payload.startswith(b'RIFF'): continue
        assert ident in convolution and len(payload)>=12,('unreviewed non-WEM media',ident)
        proofs.append(dict(Id=ident,Bytes=size,Sha256=sha(payload),Kind='WwiseConvolutionReverb'))
    return proofs
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
            recipe=dict(Id=ident,InputSha256=sha(raw),OutputSha256=sha(dst.read_bytes()),InputBytes=size,
                        OutputBytes=dst.stat().st_size,Objects=result['objects'],HircObjects=object_count(raw),Edits=result['recipe'])
            preserved=preserved_media(src,raw)
            if preserved:
                assert preserved==preserved_media(dst,dst.read_bytes()),'Convolution media changed during bank conversion'
                recipe['PreservedMedia']=preserved
            recipes.append(recipe)
            found.add(ident); print(json.dumps(dict(bank=ident,bytes=size,edits=len(result['recipe']))),flush=True)
missing=required-found
gaps=[]
if missing and a.source_gaps:
    profile=json.loads(a.source_gaps.read_bytes())
    for bank in profile['Banks']:
        if bank['Id'] in missing and any(o['Name']==bank['Name'] and o['TagSha256']==bank['TagSha256'] for o in origins):gaps.append(bank)
assert missing=={g['Id'] for g in gaps},('unreviewed missing banks',missing-{g['Id'] for g in gaps})
unique={r['InputSha256']:r for r in recipes}
if a.merge:
    previous=json.loads(a.merge.read_bytes())
    assert (previous['Format'],previous['SourceVersion'],previous['NativeVersion'])==(1,118,112)
    for recipe in previous['Recipes']:
        if recipe['InputSha256'] in unique:
            current=unique[recipe['InputSha256']].copy()
            # Older profiles did not distinguish plugin data from WEM media.
            # Enrichment is allowed only after the full-parser proofs above;
            # all pre-existing edits, sizes and bank hashes must remain exact.
            if 'PreservedMedia' not in recipe: current.pop('PreservedMedia',None)
            assert current==recipe,('changed existing recipe',recipe['InputSha256'])
        else:
            unique[recipe['InputSha256']]=recipe
a.output.parent.mkdir(parents=True,exist_ok=True)
a.output.write_text(json.dumps(dict(Format=1,SourceVersion=118,NativeVersion=112,Recipes=sorted(unique.values(),key=lambda r:r['InputSha256'])),separators=(',',':')))
(a.work/'proof.json').write_text(json.dumps(dict(Required=sorted(required),Origins=origins,Recipes=len(unique),SourceMissingBanks=gaps,MediaUnsupported=media_unsupported,ProfileSha256=sha(a.output.read_bytes())),indent=2))
print(json.dumps(dict(required=len(required),recipes=len(unique),profile_bytes=a.output.stat().st_size)),flush=True)
