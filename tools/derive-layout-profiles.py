"""Development-only profile derivation from explicitly supplied paired controls.

Emits numeric layouts only. The shipped launcher never reads these controls.
"""
import argparse, hashlib, json, struct
from pathlib import Path

PROFILES = [
    ('mat ', '20260911-014000-material-schema', 'material', '62f06d8be05554a2', '196a728b72e0a73d'),
    ('prt3', '20260911-020700-particle-schema', 'particle', '90444a3f583aa6d9', '9b7107189c991c58'),
    ('mdsv', '20260911-021500-dissolve-schema', 'dissolve', 'b122100d6716516c', '4f52a9ede21a4575'),
    ('decs', '20260911-071500-decals', 'decal', 'bef2dfe35e1fcbc4', '47ce5ee4345e3ae1'),
    ('ltvl', '20260911-072200-light-volumes', 'lightvolume', '5788fa3946a49f3e', '9e9b3ac7cce8d135'),
    ('trac', '20260911-070400-tracers', 'tracer', 'b570b7c7bc1e60a2', '077f99f1c43ac3de'),
    ('licn', '20260911-065500-light-cones', 'lightcone', '50c885d21fc9fa23', 'e7390de515209cdb'),
    ('ngst', '20260911-134000-dependency-closure', 'nodegraph', '09390d634282b033', 'dec62ce2b83a26a6'),
]

def derive(b):
    assert b[:4] == b'ucsh'
    dep, nb, ns = struct.unpack_from('<3I', b, 28)
    header, data, resource = struct.unpack_from('<3I', b, 60)
    assert header + data + resource == len(b)
    blocks = [struct.unpack_from('<I2xHQ', b, 80 + dep * 24 + i * 16) for i in range(nb)]
    def block(i):
        size, section, offset = blocks[i]
        assert section in (1, 2)
        start = header + (data if section == 2 else 0) + offset
        assert start + size <= len(b)
        return b[start:start + size]
    structures = [(b[p:p+16].hex(), *struct.unpack_from('<4i', b, p+16)) for p in
                  range(80 + dep*24 + nb*16, 80 + dep*24 + nb*16 + ns*32, 32)]
    owners, strides, specs, routes = {}, {}, {}, set()
    for guid, kind, target, parent, offset in structures:
        if target < 0: continue
        assert target < nb
        if kind in (0, 65536):
            assert parent == -1 and offset == 0
            count = 1
        else:
            assert kind in (1, 65537) and 0 <= parent < nb
            value = block(parent)
            assert 0 <= offset <= len(value)-28
            count = struct.unpack_from('<I', value, offset+16)[0]
            assert count > 0
        size = blocks[target][0]
        assert size > 0 and size % count == 0
        stride, key = size//count, (guid, kind)
        assert target not in owners or (owners[target] == key and strides[target] == stride)
        assert key not in specs or specs[key] == stride
        owners[target], strides[target], specs[key] = key, stride, stride
    for guid, kind, target, parent, offset in structures:
        if kind in (0, 65536): continue
        assert parent in owners
        routes.add((*owners[parent], offset % strides[parent], guid, kind))
    return specs, routes, structures[0][0]

def main():
    p = argparse.ArgumentParser()
    p.add_argument('archive', type=Path)
    p.add_argument('profiles', type=Path)
    p.add_argument('controls', type=Path)
    a = p.parse_args()
    profiles, controls = [], []
    def path(value):
        normalized = value.replace('\\', '/')
        marker = '/haloexport/12_forge_re/'
        assert marker in normalized.lower(), value
        return a.archive / normalized[normalized.lower().index(marker)+len(marker):]
    for group, folder, name, source, native in PROFILES:
        rows = json.loads((a.archive/folder/(name+'-controls.json')).read_text())['controls']
        specs, routes, roots, exact, variants = {}, set(), set(), 0, 0
        for row in rows:
            xp, pp = path(row['xbox']), path(row['pc'])
            x, pc = xp.read_bytes(), pp.read_bytes()
            assert f'{struct.unpack_from("<Q", x, 8)[0]:016x}' == source
            assert f'{struct.unpack_from("<Q", pc, 8)[0]:016x}' == native
            if x[:8]+x[24:] != pc[:8]+pc[24:]: variants += 1; continue
            xs, xr, root = derive(x)
            assert (xs, xr, root) == derive(pc)
            for key, size in xs.items():
                assert key not in specs or specs[key] == size
                specs[key] = size
            routes |= xr; roots.add(root); exact += 1
            controls.append(dict(Group=group, Id=row['gid'], Source=dict(Path=str(xp), Sha256=hashlib.sha256(x).hexdigest()),
                                 Native=dict(Path=str(pp), Sha256=hashlib.sha256(pc).hexdigest())))
        assert exact > 0 and len(roots) == 1
        if group == 'mdsv':
            key = ('995fedf16846ff068ffc2e8ce3cef52e', 1)
            assert key not in specs and any(r[3:] == key for r in routes)
            specs[key] = 8
        profiles.append(dict(Group=group, RootGuid=next(iter(roots)), SourceSchema=source, NativeSchema=native,
                             Strides=[dict(Guid=k[0], Kind=k[1], Size=v) for k,v in sorted(specs.items())],
                             Routes=[dict(OwnerGuid=r[0], OwnerKind=r[1], Offset=r[2], ChildGuid=r[3], ChildKind=r[4]) for r in sorted(routes)]))
        print(group, 'exact controls', exact, 'authored variants', variants, 'strides', len(specs), 'routes', len(routes))
    a.profiles.write_text(json.dumps(profiles, indent=2)+'\n')
    a.controls.write_text(json.dumps(controls, indent=2)+'\n')

if __name__ == '__main__': main()
