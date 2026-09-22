"""Validação do contrato de intercâmbio phyre.project.v1."""
import json
from pathlib import Path
import unittest

import jsonschema

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = json.loads((ROOT / "schema" / "phyre-project-v1.schema.json").read_text())


def minimal_doc():
    return {
        "schema": "phyre.project.v1",
        "source": {"path": "x.phyre", "sha256": "0" * 64},
        "nodes": [{"id": "PMeshSegment@0x10", "mesh": "m1"}],
        "meshes": [{"id": "m1", "bounds": {"min": [0, 0, 0], "max": [1, 1, 1]},
                    "primitives": [{"attributes": {"POSITION": [[0, 0, 0]]},
                                    "indices": [0], "material": "mat1"}]}],
        "rigs": [],
        "animations": [{"id": "a1", "channels": [], "extensions": {
            "ffx_motion": {"codec": "mgrp-v1"}}}],
        "materials": [{"id": "mat1", "parameters": {"unk": "kept"}}],
        "opaque": [{"kind": "unknown_block", "reason": "preserved"}],
        "loss": {"dropped": [], "approximated": [], "unsupported": []},
    }


class InterchangeTests(unittest.TestCase):
    def test_minimal_valid(self):
        jsonschema.validate(minimal_doc(), SCHEMA)

    def test_requires_schema_const(self):
        doc = minimal_doc(); doc["schema"] = "phyre.project.v0"
        with self.assertRaises(jsonschema.ValidationError):
            jsonschema.validate(doc, SCHEMA)

    def test_bad_sha256_rejected(self):
        doc = minimal_doc(); doc["source"]["sha256"] = "nothex"
        with self.assertRaises(jsonschema.ValidationError):
            jsonschema.validate(doc, SCHEMA)

    def test_loss_sections_required(self):
        doc = minimal_doc(); doc["loss"] = {"dropped": []}
        with self.assertRaises(jsonschema.ValidationError):
            jsonschema.validate(doc, SCHEMA)

    def test_unknown_top_level_rejected(self):
        doc = minimal_doc(); doc["surprise"] = True
        with self.assertRaises(jsonschema.ValidationError):
            jsonschema.validate(doc, SCHEMA)

    def test_mesh_requires_bounds(self):
        doc = minimal_doc(); del doc["meshes"][0]["bounds"]
        with self.assertRaises(jsonschema.ValidationError):
            jsonschema.validate(doc, SCHEMA)


if __name__ == "__main__":
    unittest.main()
