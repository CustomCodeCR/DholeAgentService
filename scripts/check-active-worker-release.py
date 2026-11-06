#!/usr/bin/env python3
"""Fail closed unless exactly one supported Agent worker is running."""
import importlib.util
from pathlib import Path
import os
spec = importlib.util.spec_from_file_location('owner', Path('scripts/production-generation-executor.py'))
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)
generation = mod.find_running_generation()[0]
if generation not in ('prod1', 'prod2', 'prod3'):
    raise SystemExit(f'Unsupported Agent generation {generation}: release stopped')
with open(os.environ['GITHUB_ENV'], 'a', encoding='utf-8') as f:
    f.write(f'ACTIVE_AGENT_GENERATION={generation}\n')
print(f'Active production Agent generation verified: {generation}')
