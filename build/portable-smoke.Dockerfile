# Exact linux/amd64 Ubuntu 24.04 image digest from the official Docker Hub API.
# Source: https://hub.docker.com/v2/repositories/library/ubuntu/tags/24.04
FROM ubuntu:24.04@sha256:a2d43bffc46670d7108bb3c0468c67c5557c4c92e83e1e34cbff1322ad1b94dc

RUN DEBIAN_FRONTEND=noninteractive apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install --no-install-recommends -y \
        ca-certificates libc6 libgcc-s1 libstdc++6 zlib1g libssl3t64 libicu74 \
        libgssapi-krb5-2 libbrotli1 libgmp10 tzdata libx11-6 libice6 libsm6 libfontconfig1 \
        libgl1 libegl1 libxrandr2 libxcursor1 libxi6 libxinerama1 \
    && mkdir -p /usr/local/share/tracktrim-smoke \
    && printf '%s\n' 'Base image: ubuntu:24.04@sha256:a2d43bffc46670d7108bb3c0468c67c5557c4c92e83e1e34cbff1322ad1b94dc' > /usr/local/share/tracktrim-smoke/environment.txt \
    && cat /etc/os-release >> /usr/local/share/tracktrim-smoke/environment.txt \
    && dpkg-query --show --showformat='${Package}\t${Version}\t${Architecture}\n' >> /usr/local/share/tracktrim-smoke/environment.txt \
    && rm -rf /var/lib/apt/lists/*

COPY container-entrypoint.sh /usr/local/bin/tracktrim-package-smoke
RUN chmod 755 /usr/local/bin/tracktrim-package-smoke
USER 65532:65532
ENTRYPOINT ["/usr/local/bin/tracktrim-package-smoke"]
