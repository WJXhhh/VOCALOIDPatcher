"""Owned stationary material for full native commit experiments."""
import ctypes as c
from audit_frm2_numeric import P, I, Q, F, B


def stationary(n, state, context, voice, table, sta, art, phoneme='a',
               stationary_layers=None):
    normal, unit, part = n.alloc(0x180), n.alloc(0x180), n.alloc(0x280)
    parts = [part] + ([n.alloc(0x280) for _ in stationary_layers[1:]]
                      if stationary_layers else [])
    unit_name = phoneme.encode('ascii') + b'\0'
    assert len(unit_name) <= 16
    for node, name, magic in [(normal, b'normal\0', 0), (unit, unit_name, 0x75415453)]:
        n.fn(0x1067f0, P, P, I)(node, magic)
        c.memmove(node + 0x18, name, len(name))
    for parent, children in [(sta, [normal]), (normal, [unit]), (unit, parts)]:
        P.from_address(parent + 0x148).value = n.array(P, children)
        I.from_address(parent + 0x150).value = len(children)
    phoneme_index = n.fn(0x125140, I, P, c.c_char_p)(table, phoneme.encode('ascii'))
    assert phoneme_index >= 0
    I.from_address(unit + 0x160).value = phoneme_index
    callback = c.CFUNCTYPE(P, P)(lambda unused: unit)
    n.keep.append(callback)
    vtable = n.alloc(0x68)
    P.from_address(vtable + 0x60).value = c.cast(callback, P).value
    for index, current_part in enumerate(parts):
        spec = stationary_layers[index] if stationary_layers else None
        P.from_address(current_part).value = vtable
        F.from_address(current_part + 0x160).value = (
            spec['pitch1'] if spec else 0)
        F.from_address(current_part + 0x16c).value = .6
        I.from_address(current_part + 0x1b0).value = (
            spec['frames'] if spec else 100)
        groups = spec['groups'] if spec else [(0, 100, 10, 90)]
        alignment = n.array(I, [value for group in groups for value in group])
        P.from_address(current_part + 0x1d8).value = alignment
        P.from_address(current_part + 0x1e0).value = alignment + len(groups) * 16
        if spec:
            assert len(spec['frame_offsets']) == spec['frames']
            P.from_address(current_part + 0x1a8).value = n.array(Q, spec['frame_offsets'])
            Q.from_address(current_part + 0x1b8).value = spec['snd']
            Q.from_address(current_part + 0x1c0).value = spec['core']
            I.from_address(current_part + 0x1d0).value = spec['pcm_count']
            I.from_address(current_part + 0x200).value = 0
    lookup = n.fn(0x1ab200, P, P, c.c_char_p, c.c_char_p, P, P, B)
    assert lookup(state, b'normal', phoneme.encode('ascii'), sta, table, 1) == unit
    # Long notes consult a separate bank/phoneme/dynamics stationary index.
    # A loaded voice registry alone does not initialize this state-owned map.
    sentinel = n.fn(0x216454, P, Q)(0x38)
    for offset in [0, 8, 16]:
        P.from_address(sentinel + offset).value = sentinel
    B.from_address(sentinel + 0x18).value = 1
    B.from_address(sentinel + 0x19).value = 1
    P.from_address(state + 0x4098).value = sentinel
    n.fn(0x1a74a0, None, P)(state)
    assert Q.from_address(state + 0x40a0).value == 1
    found, key = n.alloc(8), n.array(I, [0, 0])
    n.fn(0x1a9f20, P, P, P, P)(state + 0x4098, found, key)
    bank_node = P.from_address(found).value
    assert bank_node != sentinel and Q.from_address(bank_node + 0x30).value == 1
    return dict(part=part, unit=unit, parts=parts,
                stationary_parts=parts.copy())


def connected(n, state, context, voice, table, sta, art, edges=(('Sil', 'a'), ('a', 'Sil')),
              tree=False, edge_layers=None, stationary_phoneme='a',
              stationary_layers=None, triple_layers=None):
    result = stationary(n, state, context, voice, table, sta, art,
                        phoneme=stationary_phoneme,
                        stationary_layers=stationary_layers)
    result['groups_by_part'] = {}
    result['edge_parts'] = {}
    parents = {}
    callback = c.CFUNCTYPE(P, P)(lambda node: parents.get(node, 0))
    n.keep.append(callback)
    vtable = n.alloc(0x68)
    P.from_address(vtable + 0x60).value = c.cast(callback, P).value
    roots = {}
    children = {}
    edge_nodes = {}
    for left, right in edges:
        if tree and left in roots:
            first = roots[left]
        else:
            first = n.alloc(0x180)
            P.from_address(first).value = vtable
            raw = left.encode() + b'\0'
            c.memmove(first + 0x18, raw, len(raw))
            parents[first] = art
            if tree:
                roots[left] = first
                children[left] = []
        previous = first
        for name in [right]:
            node = n.alloc(0x180)
            if tree:
                n.fn(0x1067f0, P, P, I)(node, 0x75545241)
                phoneme_index = n.fn(0x125140, I, P, c.c_char_p)(table, name.encode())
                assert phoneme_index >= 0
                I.from_address(node + 0x160).value = phoneme_index
            P.from_address(node).value = vtable
            raw = name.encode() + b'\0'
            c.memmove(node + 0x18, raw, len(raw))
            parents[node] = previous
            previous = node
            if tree:
                children[left].append(node)
                edge_nodes[(left, right)] = node
        layers = (edge_layers or {}).get((left, right))
        if layers is None:
            layers = [dict(pitch1=0, pitch2=0, dynamics=.6, frames=100,
                           groups=[(0, 50, 5, 45), (50, 100, 55, 95)])]
        parts = []
        for layer in layers:
            part = n.alloc(0x280)
            P.from_address(part).value = vtable
            parents[part] = node
            F.from_address(part + 0x160).value = layer['pitch1']
            F.from_address(part + 0x164).value = layer['pitch2']
            F.from_address(part + 0x16c).value = layer['dynamics']
            I.from_address(part + 0x1b0).value = layer['frames']
            groups = layer['groups']
            alignment = n.array(I, [value for group in groups for value in group])
            P.from_address(part + 0x1d8).value = alignment
            P.from_address(part + 0x1e0).value = alignment + len(groups) * 16
            if 'frame_offsets' in layer:
                assert len(layer['frame_offsets']) == layer['frames']
                P.from_address(part + 0x1a8).value = n.array(Q, layer['frame_offsets'])
                Q.from_address(part + 0x1b8).value = layer['snd']
                Q.from_address(part + 0x1c0).value = layer['core']
                I.from_address(part + 0x1d0).value = layer['pcm_count']
                I.from_address(part + 0x200).value = 0
            parts.append(part)
            result['parts'].append(part)
            result['groups_by_part'][part] = groups
        P.from_address(node + 0x148).value = n.array(P, parts)
        I.from_address(node + 0x150).value = len(parts)
        n.fn(0x18d790, None, P, P)(voice + 0x2e0, node)
        result['edge_parts'][(left, right)] = parts

    result['triple_parts'] = {}
    if triple_layers:
        assert not tree
        for key, layers in triple_layers.items():
            assert len(key) == 3 and key[1].startswith('^') and layers
            previous = art
            for name in key:
                node = n.alloc(0x180)
                P.from_address(node).value = vtable
                raw = name.encode('ascii') + b'\0'
                c.memmove(node + 0x18, raw, len(raw))
                parents[node] = previous
                previous = node
            parts = []
            for layer in layers:
                part = n.alloc(0x280)
                P.from_address(part).value = vtable
                parents[part] = node
                F.from_address(part + 0x160).value = layer['pitch1']
                F.from_address(part + 0x164).value = layer['pitch2']
                F.from_address(part + 0x16c).value = layer['dynamics']
                I.from_address(part + 0x1b0).value = layer['frames']
                groups = layer['groups']
                assert len(groups) == 4
                alignment = n.array(I, [value for group in groups for value in group])
                P.from_address(part + 0x1d8).value = alignment
                P.from_address(part + 0x1e0).value = alignment + len(groups) * 16
                if 'frame_offsets' in layer:
                    assert len(layer['frame_offsets']) == layer['frames']
                    P.from_address(part + 0x1a8).value = n.array(Q, layer['frame_offsets'])
                    Q.from_address(part + 0x1b8).value = layer['snd']
                    Q.from_address(part + 0x1c0).value = layer['core']
                    I.from_address(part + 0x1d0).value = layer['pcm_count']
                    I.from_address(part + 0x200).value = 0
                parts.append(part)
                result['parts'].append(part)
                result['groups_by_part'][part] = groups
            P.from_address(node + 0x148).value = n.array(P, parts)
            I.from_address(node + 0x150).value = len(parts)
            n.fn(0x18d790, None, P, P)(voice + 0x2e0, node)
            result['triple_parts'][key] = parts
    if tree:
        P.from_address(art + 0x148).value = n.array(P, list(roots.values())) if roots else 0
        I.from_address(art + 0x150).value = len(roots)
        for left, first in roots.items():
            P.from_address(first + 0x148).value = n.array(P, children[left])
            I.from_address(first + 0x150).value = len(children[left])
        find = n.fn(0x1076b0, P, P, c.c_char_p)
        assert find(voice, b'voice') != 0
        assert find(find(voice, b'voice'), b'articulation') == art
        for (left, right), node in edge_nodes.items():
            assert find(find(art, left.encode()), right.encode()) == node
            phoneme_index = n.fn(0x125140, I, P, c.c_char_p)(table, right.encode())
            assert n.fn(0x10a350, P, P, I)(find(art, left.encode()), phoneme_index) == node
        result['tree_units'] = edge_nodes
    assert Q.from_address(voice + 0x2e8).value == (
        sum(len(parts) for parts in result['edge_parts'].values()) +
        2 * sum(len(parts) for parts in result['triple_parts'].values()))
    return result
