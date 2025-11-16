#!/bin/bash

###############################################################################
# Cleanup Script - Orleans Ticketing Backend
#
# Removes all deployed resources from Kubernetes
#
# Usage:
#   ./scripts/cleanup.sh
###############################################################################

set -e

echo "========================================="
echo "Orleans Ticketing - Cleanup"
echo "========================================="
echo ""

echo "WARNING: This will delete all resources in the 'ticketing' namespace."
read -p "Are you sure you want to continue? (yes/no): " -r
echo

if [[ ! $REPLY =~ ^[Yy]es$ ]]; then
    echo "Cleanup cancelled."
    exit 0
fi

echo "Deleting all resources in namespace 'ticketing'..."
kubectl delete -f deploy/k8s/ -n ticketing >/dev/null 2>&1 || true
kubectl delete deployment redis -n ticketing >/dev/null 2>&1 || true
kubectl delete service redis-master -n ticketing >/dev/null 2>&1 || true

echo "Deleting namespace 'ticketing'..."
kubectl delete namespace ticketing >/dev/null 2>&1 || true

echo ""
echo "✓ Cleanup completed successfully"
echo ""
echo "All resources have been removed."
