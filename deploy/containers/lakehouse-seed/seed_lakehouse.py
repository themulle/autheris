#!/usr/bin/env python3
"""
Seed script for MinIO S3 Object Storage (Apache Iceberg Lakehouse Extension).
Creates the 'analytics-lake' bucket and uploads Iceberg v2 metadata,
manifest-list, and Parquet data chunks.
"""

import os
import sys
import time
from pathlib import Path

def get_env(key, default):
    return os.environ.get(key, default)

def main():
    endpoint = get_env("MINIO_ENDPOINT", "http://minio:9000")
    access_key = os.environ["MINIO_ACCESS_KEY"]
    secret_key = os.environ["MINIO_SECRET_KEY"]
    bucket_name = get_env("LAKEHOUSE_BUCKET", "analytics-lake")
    ready_file = get_env("READY_FILE", "/tmp/.lakehouse_seed_complete")

    print(f"[lakehouse-seed] Initializing Lakehouse storage on MinIO: {endpoint}")

    try:
        import boto3
        from botocore.client import Config
        from botocore.exceptions import ClientError
    except ImportError:
        # boto3 is pinned and installed at image build time; no runtime package installation.
        print("[lakehouse-seed] ERROR: boto3 is not installed in this image. Rebuild the image.", file=sys.stderr)
        sys.exit(1)

    s3 = boto3.client(
        "s3",
        endpoint_url=endpoint,
        aws_access_key_id=access_key,
        aws_secret_access_key=secret_key,
        config=Config(signature_version="s3v4"),
        region_name="us-east-1"
    )

    # 1. Wait for MinIO readiness
    max_retries = 30
    ready = False
    for i in range(1, max_retries + 1):
        try:
            s3.list_buckets()
            ready = True
            print(f"[lakehouse-seed] Successfully connected to MinIO after {i} attempt(s).")
            break
        except Exception as e:
            print(f"[lakehouse-seed] Waiting for MinIO ({i}/{max_retries}): {e}")
            time.sleep(2)

    if not ready:
        print("[lakehouse-seed] ERROR: MinIO did not become ready in time.")
        sys.exit(1)

    # 2. Ensure bucket exists
    try:
        s3.head_bucket(Bucket=bucket_name)
        print(f"[lakehouse-seed] Bucket '{bucket_name}' already exists.")
    except ClientError:
        print(f"[lakehouse-seed] Creating bucket '{bucket_name}'...")
        s3.create_bucket(Bucket=bucket_name)

    # 3. Upload Iceberg Table Files
    base_dir = Path(__file__).resolve().parent / "sample_iceberg"
    files_to_upload = [
        ("v2.metadata.json", "tables/orders/metadata/v2.metadata.json", "application/json"),
        ("snap-801122334455-manifest-list.json", "tables/orders/metadata/snap-801122334455-manifest-list.json", "application/json"),
        ("orders-20260601-alpha.parquet", "tables/orders/data/orders-20260601-alpha.parquet", "application/octet-stream"),
        ("orders-20260701-beta.parquet", "tables/orders/data/orders-20260701-beta.parquet", "application/octet-stream")
    ]

    for local_name, s3_key, content_type in files_to_upload:
        file_path = base_dir / local_name
        if file_path.exists():
            print(f"[lakehouse-seed] Uploading {local_name} -> s3://{bucket_name}/{s3_key}")
            with open(file_path, "rb") as f:
                s3.put_object(
                    Bucket=bucket_name,
                    Key=s3_key,
                    Body=f.read(),
                    ContentType=content_type
                )
        else:
            print(f"[lakehouse-seed] WARNING: {file_path} not found!")

    # 4. Mark seed complete
    Path(ready_file).touch()
    print(f"[lakehouse-seed] Lakehouse seeding complete! Marker written to {ready_file}")

if __name__ == "__main__":
    main()
