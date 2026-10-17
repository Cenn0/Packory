# Pinned upstream source commit; update only after reproducible tests and license review.
FROM python:3.12.7-slim-bookworm
ENV PIP_DISABLE_PIP_VERSION_CHECK=1 PYTHONDONTWRITEBYTECODE=1
RUN useradd --uid 15001 --no-create-home --home-dir /tmp scanner
RUN pip install --no-cache-dir 'skillspector @ git+https://github.com/NVIDIA/SkillSpector.git@091a509b3d6ba8ff5be528966e1f43c2e68cf214'
COPY --chown=scanner:scanner worker.py /opt/packory/worker.py
USER 15001:15001
WORKDIR /tmp
ENTRYPOINT ["python", "/opt/packory/worker.py"]
