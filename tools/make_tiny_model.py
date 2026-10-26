"""Makes tiny-chat.gguf: a very small model with random weights, for the tests.

It talks gibberish, but it is a real model file, so the tests can check that
Scimilir loads models, writes the chat in the model's format and streams the
reply, without downloading gigabytes. Needs: pip install gguf numpy
Run: python tools/make_tiny_model.py Scimilir.Tests/Assets/tiny-chat.gguf
"""
import sys

import gguf
import numpy as np

out = sys.argv[1] if len(sys.argv) > 1 else "tiny-chat.gguf"
rng = np.random.default_rng(42)

# Tokens: the special ones, then one per byte, then a few common words.
specials = ["<unk>", "<s>", "</s>", "<|im_start|>", "<|im_end|>"]
byte_tokens = [f"<0x{b:02X}>" for b in range(256)]
words = ["▁" + w for w in "hello world the a is of and to in it you I user assistant system".split()]
words += ["▁", "user", "assistant", "system"]
tokens = specials + byte_tokens + words
types = ([gguf.TokenType.UNKNOWN, gguf.TokenType.CONTROL, gguf.TokenType.CONTROL, gguf.TokenType.CONTROL, gguf.TokenType.CONTROL]
         + [gguf.TokenType.BYTE] * 256 + [gguf.TokenType.NORMAL] * len(words))
scores = [0.0] * len(specials) + [0.0] * 256 + [-float(i) for i in range(len(words))]

n_vocab, n_embd, n_head, n_ff, n_layer = len(tokens), 32, 4, 64, 1

w = gguf.GGUFWriter(out, "llama")
w.add_name("tiny-chat")
w.add_context_length(2048)
w.add_embedding_length(n_embd)
w.add_block_count(n_layer)
w.add_feed_forward_length(n_ff)
w.add_head_count(n_head)
w.add_head_count_kv(n_head)
w.add_rope_dimension_count(n_embd // n_head)
w.add_layer_norm_rms_eps(1e-5)
w.add_vocab_size(n_vocab)
w.add_file_type(gguf.LlamaFileType.ALL_F32)

w.add_tokenizer_model("llama")
w.add_token_list(tokens)
w.add_token_scores(scores)
w.add_token_types(types)
w.add_unk_token_id(0)
w.add_bos_token_id(1)
w.add_eos_token_id(4)  # <|im_end|> ends a reply
w.add_add_bos_token(True)
w.add_chat_template(
    "{% for message in messages %}{{'<|im_start|>' + message['role'] + '\n' + message['content'] + '<|im_end|>' + '\n'}}"
    "{% endfor %}{% if add_generation_prompt %}{{ '<|im_start|>assistant\n' }}{% endif %}")


def t(*shape):
    return (rng.standard_normal(shape) * 0.2).astype(np.float32)


w.add_tensor("token_embd.weight", t(n_vocab, n_embd))
w.add_tensor("output_norm.weight", np.ones(n_embd, dtype=np.float32))
w.add_tensor("output.weight", t(n_vocab, n_embd))
for i in range(n_layer):
    w.add_tensor(f"blk.{i}.attn_norm.weight", np.ones(n_embd, dtype=np.float32))
    w.add_tensor(f"blk.{i}.attn_q.weight", t(n_embd, n_embd))
    w.add_tensor(f"blk.{i}.attn_k.weight", t(n_embd, n_embd))
    w.add_tensor(f"blk.{i}.attn_v.weight", t(n_embd, n_embd))
    w.add_tensor(f"blk.{i}.attn_output.weight", t(n_embd, n_embd))
    w.add_tensor(f"blk.{i}.ffn_norm.weight", np.ones(n_embd, dtype=np.float32))
    w.add_tensor(f"blk.{i}.ffn_gate.weight", t(n_ff, n_embd))
    w.add_tensor(f"blk.{i}.ffn_up.weight", t(n_ff, n_embd))
    w.add_tensor(f"blk.{i}.ffn_down.weight", t(n_embd, n_ff))

w.write_header_to_file()
w.write_kv_data_to_file()
w.write_tensors_to_file()
w.close()
print("wrote", out)
