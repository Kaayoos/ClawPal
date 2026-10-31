"""Regression checks for translator mistakes and English fallback; no device needed."""
import importlib.util,json,tempfile,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('catalogs',ROOT/'scripts/generate_languages.py')
catalogs=importlib.util.module_from_spec(spec);spec.loader.exec_module(catalogs)
class CatalogChecks(unittest.TestCase):
    def setUp(self):
        self.folder=tempfile.TemporaryDirectory();self.addCleanup(self.folder.cleanup);self.root=Path(self.folder.name)
        (self.root/'languages').mkdir();(self.root/'firmware/main').mkdir(parents=True);(self.root/'companion').mkdir()
        self.en=json.loads((ROOT/'languages/en.json').read_text(encoding='utf-8'));self.pl=json.loads((ROOT/'languages/pl.json').read_text(encoding='utf-8'))
        self.save()
    def save(self):
        for code,pack in [('en',self.en),('pl',self.pl)]: (self.root/'languages'/f'{code}.json').write_text(json.dumps(pack,ensure_ascii=False),encoding='utf-8')
    def test_complete_release_catalogs(self):
        for section in ['companion','display']:self.assertEqual(set(self.en[section]),set(self.pl[section]))
        catalogs.generate(self.root);catalogs.generate(self.root,True)
    def test_missing_translation_falls_back_to_english(self):
        self.pl['display'].pop('welcome_home');self.save();catalogs.generate(self.root)
        generated=(self.root/'firmware/main/language/buddy_language_data.c').read_text(encoding='utf-8')
        self.assertIn('text_1_welcome_home[] = "Welcome home"',generated)
    def test_wrong_printf_type_rejected(self):
        for value in ['Odnowienie: %n', 'Odnowienie: %s %*s', 'Odnowienie: %s %1$s', 'Odnowienie: %s %']:
            with self.subTest(value=value):
                self.pl['display']['resets_value']=value;self.save()
                with self.assertRaises(ValueError):catalogs.generate(self.root)
    def test_missing_dotnet_argument_rejected(self):
        self.pl['companion']['versions']='Ekran {0}';self.save()
        with self.assertRaises(ValueError):catalogs.generate(self.root)
    def test_unknown_translation_key_rejected(self):
        self.pl['display']['typo']='Tekst';self.save()
        with self.assertRaises(ValueError):catalogs.generate(self.root)
    def test_catalog_edits_require_regeneration(self):
        catalogs.generate(self.root);self.pl['display']['welcome_home']='Witaj!';self.save()
        with self.assertRaises(ValueError):catalogs.generate(self.root,True)
    def test_missing_glyph_rejected(self):
        catalogs.generate(self.root)
        (self.root/'firmware/main/language/font_coverage.json').write_text('[32]')
        with self.assertRaises(ValueError):catalogs.generate(self.root)
    def test_repeated_translations_have_distinct_addresses(self):
        self.pl['display']['clock']=self.pl['display']['home']='Ten sam tekst';self.save();catalogs.generate(self.root)
        generated=(self.root/'firmware/main/language/buddy_language_data.c').read_text(encoding='utf-8')
        self.assertIn('text_1_clock[] = "Ten sam tekst"',generated);self.assertIn('text_1_home[] = "Ten sam tekst"',generated)
    def test_studio_wording_overrides_existing_keys_only(self):
        self.assertEqual(set(self.en['display_studio']),set(self.pl['display_studio']))
        self.pl['display_studio']['typo']='Tekst';self.save()
        with self.assertRaises(ValueError):catalogs.generate(self.root)
    def test_studio_wording_keeps_placeholders(self):
        self.en['display_studio']['resets_value']='Resets %d';self.save()
        with self.assertRaises(ValueError):catalogs.generate(self.root)
    def test_studio_wording_compiles_beside_the_base_text(self):
        catalogs.generate(self.root);generated=(self.root/'firmware/main/language/buddy_language_data.c').read_text(encoding='utf-8')
        self.assertIn('[BT_YOUR_APPS_AND_YOUR_CRAB] = studio_0_your_apps_and_your_crab',generated)
        self.assertIn('text_0_your_apps_and_your_crab[] = "Your apps and your crab"',generated)
if __name__=='__main__':unittest.main()
