# Delta Lake on the pinned Spark proxy image (WP-C4, WP-A7, SEC-ADG-24).
#
# UPDATE, DELETE and MERGE need a transactional table format; the plain apache/spark image has none. The two Delta jars are
# downloaded at IMAGE BUILD time and verified against the SHA-256 recorded in fetch-delta.py, never at test run time (no
# --packages, no ivy resolution). A changed or tampered jar fails the build.
FROM apache/spark:4.0.0-python3@sha256:9e2f63442ba1a672ea70d780d56da28f42ce515d0be18840569a3054cc6d2314

USER root
COPY fetch-delta.py /tmp/fetch-delta.py
RUN python3 /tmp/fetch-delta.py /opt/spark/jars && rm /tmp/fetch-delta.py
