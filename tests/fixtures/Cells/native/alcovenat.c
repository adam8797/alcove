/* Native test library: built twice by the integration tests, with -DALCOVENAT_VERSION=1 and =2. */
#ifndef ALCOVENAT_VERSION
#define ALCOVENAT_VERSION 0
#endif
int alcovenat_version(void) { return ALCOVENAT_VERSION; }
