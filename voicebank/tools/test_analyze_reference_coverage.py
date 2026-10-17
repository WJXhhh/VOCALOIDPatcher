import unittest

from analyze_reference_coverage import coverage, pitch_inventory
from verify_reference_ddi import AuditError


def parts(*pitches):
    return [{'pitch1': pitch} for pitch in pitches]


class CoverageTests(unittest.TestCase):
    def test_empty_pair_does_not_supply_attack(self):
        result = coverage([(('a',), parts(0))], [(('Sil', 'a'), []), (('a', 'Sil'), parts(0))])
        self.assertEqual(result['pair_graph']['edges'], 1)
        self.assertEqual(result['pair_graph']['sta_without_direct_sil_attack'], ['a'])
        self.assertEqual(result['pair_graph']['nodes_unreachable_from_sil'], ['a'])

    def test_triple_does_not_create_two_pair_edges(self):
        result = coverage([], [(('Sil', 'a#2', 'a'), parts(0))])
        self.assertEqual(result['pair_graph']['edges'], 0)
        self.assertEqual(result['by_kind']['ART3']['material_keys'], 1)

    def test_style_tokens_stay_distinct_and_paths_are_directed(self):
        result = coverage([(('a#2',), parts(0))],
                          [(('Sil', 'a'), parts(0)), (('a', 'a#2'), parts(0))])
        graph = result['pair_graph']
        self.assertEqual(graph['nodes_unreachable_from_sil'], [])
        self.assertEqual(graph['sta_without_direct_sil_attack'], ['a#2'])
        self.assertEqual(graph['nodes_without_route_to_sil'], ['a', 'a#2'])

    def test_sentinel_is_material_but_not_pitch_layer(self):
        inv = pitch_inventory(parts(-3.4028234663852886e38, 0, 0, 100))
        self.assertEqual((inv['parts'], inv['sentinel_parts'], inv['distinct_exact_pitch1']), (4, 1, 2))

    def test_duplicate_key_rejected(self):
        with self.assertRaises(AuditError):
            coverage([(('a',), []), (('a',), parts(0))], [])

    def test_nonfinite_rejected(self):
        with self.assertRaises(AuditError):
            pitch_inventory(parts(float('nan')))


if __name__ == '__main__':
    unittest.main()
