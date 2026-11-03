"""Imports generate-kits.py (a dash in the file name keeps it out of `import`) without running its rebuild."""
import importlib.util, os

_spec = importlib.util.spec_from_file_location('generate_kits', os.path.join(os.path.dirname(os.path.abspath(__file__)), 'generate-kits.py'))
module = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(module)
