/* instance_test.c -- does an OpenXR program of THIS size (32- or 64-bit) still get an instance with Hand Freeze ON?
 *
 * Why (2026-10-14): a 32-bit game (Manhunt) could not start OpenXR with Hand Freeze switched on: the per-user layer list
 * is read by 32-bit and 64-bit loaders alike, and a loader that can load no layer from the list fails xrCreateInstance.
 * The fix registers a 32-bit copy of the layer beside the 64-bit one. This program, built once as x86 and once as x64,
 * loads the OpenXR loader DLL given on the command line, asks for an instance with XR_LOADER_DEBUG=all on, and prints
 * the result. The loader's own log (stderr) says which layers it skipped and whether layer loading failed as a whole.
 *
 *   instance_test.exe <path to openxr_loader.dll of the same size>
 * Prints: "INSTANCE: ok" or "INSTANCE: failed (code N)". Exit 0 only on ok.
 */
#define XR_NO_PROTOTYPES
#include <windows.h>
#include <openxr/openxr.h>
#include <stdio.h>
#include <string.h>

int main(int argc, char** argv) {
    if (argc < 2) { fprintf(stderr, "usage: instance_test <openxr_loader.dll>\n"); return 2; }
    _putenv("XR_LOADER_DEBUG=all");
    HMODULE lib = LoadLibraryA(argv[1]);
    if (lib == NULL) { printf("LOADER: could not load %s (error %lu)\n", argv[1], GetLastError()); return 2; }
    PFN_xrGetInstanceProcAddr gipa = (PFN_xrGetInstanceProcAddr)GetProcAddress(lib, "xrGetInstanceProcAddr");
    PFN_xrCreateInstance create = NULL;
    if (gipa == NULL || XR_FAILED(gipa(XR_NULL_HANDLE, "xrCreateInstance", (PFN_xrVoidFunction*)&create)) || create == NULL) {
        printf("LOADER: no xrCreateInstance\n");
        return 2;
    }
    XrInstanceCreateInfo info;
    memset(&info, 0, sizeof info);
    info.type = XR_TYPE_INSTANCE_CREATE_INFO;
    strcpy_s(info.applicationInfo.applicationName, XR_MAX_APPLICATION_NAME_SIZE, "hand-freeze-instance-test");
    info.applicationInfo.applicationVersion = 1;
    strcpy_s(info.applicationInfo.engineName, XR_MAX_ENGINE_NAME_SIZE, "none");
    info.applicationInfo.apiVersion = XR_API_VERSION_1_0;   // the oldest loaders in our game builds speak 1.0
    XrInstance instance = XR_NULL_HANDLE;
    XrResult r = create(&info, &instance);
    printf("SIZE: %u-bit\n", (unsigned)(sizeof(void*) * 8));
    if (XR_SUCCEEDED(r)) {
        printf("INSTANCE: ok\n");
        PFN_xrDestroyInstance destroy = NULL;
        if (XR_SUCCEEDED(gipa(instance, "xrDestroyInstance", (PFN_xrVoidFunction*)&destroy)) && destroy != NULL) destroy(instance);
        return 0;
    }
    printf("INSTANCE: failed (code %d)\n", (int)r);
    return 1;
}
