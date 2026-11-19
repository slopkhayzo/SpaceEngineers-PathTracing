/* The C declaration of the D3D11 interfaces spells out their vtables, so the
   slot of a method is a compile-time constant instead of a hand-counted one. */
#define CINTERFACE
#define COBJMACROS
/* The SDK's C declarations trip /W4 (nameless unions and the like). */
#pragma warning(push, 0)
#include <d3d11.h>
#pragma warning(pop)
#include <stddef.h>

int SertExecuteCommandListVtableIndex(void) {
    return (int)(offsetof(ID3D11DeviceContextVtbl, ExecuteCommandList) / sizeof(void*));
}
