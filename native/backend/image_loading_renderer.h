// RmlUi's renderers read only TGA. ImageLoadingRenderer adds PNG and JPEG (through stb_image), the same for every
// renderer, and premultiplies alpha as RmlUi's render interfaces expect. TGA still goes to the renderer's own loader.
// stb_image's implementation is compiled in rmlui_c_backend.cpp.
#pragma once

#include <RmlUi/Core/FileInterface.h>
#include <RmlUi/Core/Log.h>
#include <RmlUi/Core/StringUtilities.h>
#include <RmlUi/Core/Types.h>
#include <climits>

#define STBI_ONLY_PNG
#define STBI_ONLY_JPEG
#define STBI_NO_STDIO
#include <stb_image.h>

template <class Renderer>
class ImageLoadingRenderer final : public Renderer
{
public:
    Rml::TextureHandle LoadTexture(Rml::Vector2i& texture_dimensions, const Rml::String& source) override
    {
        const size_t dot = source.rfind('.');
        Rml::String extension = dot == Rml::String::npos ? Rml::String() : Rml::StringUtilities::ToLower(source.substr(dot + 1));
        if (extension == "tga")
        {
            return Renderer::LoadTexture(texture_dimensions, source);
        }

        Rml::FileInterface* files = Rml::GetFileInterface();
        Rml::FileHandle file = files->Open(source);
        if (!file)
        {
            return {};
        }
        const size_t size = files->Length(file);
        Rml::Vector<Rml::byte> bytes(size);
        const size_t read = files->Read(bytes.data(), size, file);
        files->Close(file);
        if (read != size || size > size_t(INT_MAX))
        {
            Rml::Log::Message(Rml::Log::LT_WARNING, "Could not read image %s", source.c_str());
            return {};
        }

        int width = 0, height = 0, channels = 0;
        stbi_uc* pixels = stbi_load_from_memory(bytes.data(), int(size), &width, &height, &channels, 4);
        if (!pixels)
        {
            Rml::Log::Message(Rml::Log::LT_WARNING, "Could not decode image %s: %s", source.c_str(), stbi_failure_reason());
            return {};
        }
        const size_t byte_size = size_t(width) * size_t(height) * 4;
        for (size_t i = 0; i < byte_size; i += 4)
        {
            const int alpha = pixels[i + 3];
            for (size_t j = 0; j < 3; ++j)
            {
                pixels[i + j] = stbi_uc(int(pixels[i + j]) * alpha / 255);
            }
        }
        texture_dimensions = {width, height};
        Rml::TextureHandle handle = this->GenerateTexture({pixels, byte_size}, texture_dimensions);
        stbi_image_free(pixels);
        return handle;
    }
};
