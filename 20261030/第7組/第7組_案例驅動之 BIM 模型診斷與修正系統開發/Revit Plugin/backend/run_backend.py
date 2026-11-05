"""Launch the installed local service without a console window."""
import logging
import os
from pathlib import Path
import sys

import uvicorn

root = Path(__file__).resolve().parent
data = Path(os.environ.get('CASEBASE_DATA_DIR', root / 'data'))
data.mkdir(parents=True, exist_ok=True)
log = (data / 'server.log').open('a', encoding='utf-8', buffering=1)
sys.stdout = sys.stderr = log
logging.basicConfig(stream=log, level=logging.INFO)
(data / 'server.pid').write_text(str(os.getpid()), encoding='ascii')

if __name__ == '__main__':
    uvicorn.run('app.main:app', host='127.0.0.1', port=8000, log_level='info')
